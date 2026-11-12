using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using WinXplorer.Interop;
using WinXplorer.Models;

namespace WinXplorer.Services
{
    /// <summary>
    /// Loads thumbnails on background STA threads. Newest requests are served first (LIFO),
    /// so while scrolling the tiles currently on screen are loaded before the ones scrolled past.
    /// </summary>
    public static class ThumbnailService
    {
        private static readonly LinkedList<(FileItem item, int size)> _pending = new();
        private static readonly object _lock = new();
        private static readonly SemaphoreSlim _signal = new(0);
        private static readonly Dictionary<string, ImageSource> _cache = new();
        private const int MaxPending = 400, MaxCache = 3000;

        static ThumbnailService()
        {
            int n = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
            for (int i = 0; i < n; i++)
            {
                var t = new Thread(Worker) { IsBackground = true, Name = "Thumb" + i, Priority = ThreadPriority.BelowNormal };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
            }
        }

        private static string Key(FileItem f, int size) => f.FullPath + "|" + size + "|" + f.Modified.Ticks;

        public static void Request(FileItem item, int size)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(Key(item, size), out var img))
                {
                    Application.Current.Dispatcher.BeginInvoke(() => item.SetThumbnail(img));
                    return;
                }
                _pending.AddFirst((item, size));
                while (_pending.Count > MaxPending) _pending.RemoveLast();
            }
            _signal.Release();
        }

        public static void ClearQueue()
        {
            lock (_lock) _pending.Clear();
        }

        private static void Worker()
        {
            while (true)
            {
                _signal.Wait();
                (FileItem item, int size) job;
                lock (_lock)
                {
                    if (_pending.Count == 0) continue;
                    job = _pending.First.Value;
                    _pending.RemoveFirst();
                }
                try
                {
                    ImageSource img = null;
                    var ext = job.item.Extension;
                    if (ext == ".svg" || ext == ".svgz") img = ImageLoader.LoadSvg(job.item.FullPath);
                    else if (HeifDecoder.Handles(ext)) img = HeifDecoder.Load(job.item.FullPath, job.size, out _, out _, out _);
                    else if (EmbeddedImages.IsPsd(ext) || EmbeddedImages.HasXmpThumb(ext))
                        img = Shell.GetThumbnail(job.item.FullPath, job.size, Shell.SIIGBF_BIGGERSIZEOK | Shell.SIIGBF_THUMBNAILONLY)
                              ?? EmbeddedImages.Load(job.item.FullPath, job.size);
                    img ??= Shell.GetThumbnail(job.item.FullPath, job.size);
                    if (img == null) continue;
                    lock (_lock)
                    {
                        if (_cache.Count > MaxCache) _cache.Clear();
                        _cache[Key(job.item, job.size)] = img;
                    }
                    Application.Current?.Dispatcher.BeginInvoke(() => job.item.SetThumbnail(img));
                }
                catch { }
            }
        }
    }
}
