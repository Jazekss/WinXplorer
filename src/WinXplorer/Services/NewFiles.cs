using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WinXplorer.Services
{
    /// <summary>File types offered under "New" (chosen in Settings › New files), each with a small starter template.</summary>
    public static class NewFiles
    {
        public sealed record FileType(string Ext, string Name, string Template);

        public static readonly FileType[] Catalog =
        {
            new(".txt", null, ""),
            new(".md", "Markdown", "# Title\n\n"),
            new(".html", "HTML", "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n    <meta charset=\"UTF-8\">\n    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n    <title>Document</title>\n    <link rel=\"stylesheet\" href=\"style.css\">\n</head>\n<body>\n\n    <script src=\"script.js\"></script>\n</body>\n</html>\n"),
            new(".css", "CSS", "/* styles */\n\n"),
            new(".scss", "SCSS", ""),
            new(".js", "JavaScript", "'use strict';\n\n"),
            new(".ts", "TypeScript", ""),
            new(".jsx", "React JSX", ""),
            new(".tsx", "React TSX", ""),
            new(".vue", "Vue", "<template>\n  <div></div>\n</template>\n\n<script setup>\n</script>\n\n<style scoped>\n</style>\n"),
            new(".json", "JSON", "{\n  \n}\n"),
            new(".xml", "XML", "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<root>\n</root>\n"),
            new(".yaml", "YAML", ""),
            new(".toml", "TOML", ""),
            new(".ini", "INI", ""),
            new(".env", ".env", ""),
            new(".php", "PHP", "<?php\n\n"),
            new(".py", "Python", "def main():\n    pass\n\n\nif __name__ == \"__main__\":\n    main()\n"),
            new(".rb", "Ruby", ""),
            new(".cs", "C#", "namespace App;\n\n"),
            new(".java", "Java", ""),
            new(".kt", "Kotlin", ""),
            new(".go", "Go", "package main\n\nfunc main() {\n}\n"),
            new(".rs", "Rust", "fn main() {\n}\n"),
            new(".c", "C", "#include <stdio.h>\n\nint main(void) {\n    return 0;\n}\n"),
            new(".cpp", "C++", "#include <iostream>\n\nint main() {\n    return 0;\n}\n"),
            new(".h", "C/C++ header", "#pragma once\n\n"),
            new(".dart", "Dart", ""),
            new(".swift", "Swift", ""),
            new(".lua", "Lua", ""),
            new(".sql", "SQL", ""),
            new(".sh", "Shell script", "#!/usr/bin/env bash\nset -euo pipefail\n\n"),
            new(".ps1", "PowerShell", ""),
            new(".bat", "Batch", "@echo off\n\n"),
            new(".svg", "SVG", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"256\" height=\"256\" viewBox=\"0 0 256 256\">\n</svg>\n"),
            new(".csv", "CSV", ""),
            new(".log", "Log", ""),
        };

        public static readonly string[] Defaults = { ".txt", ".md", ".html", ".css", ".js", ".json", ".php", ".py" };

        public static List<string> Enabled => AppState.Settings.NewFileTypes is { Count: > 0 } l ? l : Defaults.ToList();

        public static string NameOf(string ext)
        {
            if (ext == ".txt") return Loc.T("TextFile");
            var t = Catalog.FirstOrDefault(c => c.Ext == ext);
            return (t?.Name ?? ext.TrimStart('.').ToUpperInvariant()) + "  (" + ext + ")";
        }

        public static string Create(string dir, string ext)
        {
            ext = Normalize(ext);
            var baseName = ext == ".txt" ? Loc.T("NewTextName") : Loc.T("NewFileName");
            var p = FileOps.UniqueName(dir, baseName, ext);
            var tpl = Catalog.FirstOrDefault(c => c.Ext == ext)?.Template ?? "";
            // same bytes File.WriteAllText wrote (UTF-8, no BOM); where the user may not write, Windows asks for permission
            var bytes = new System.Text.UTF8Encoding(false).GetBytes(tpl.Replace("\n", Environment.NewLine));
            return FileOps.WriteNew(p, fs => fs.Write(bytes)) ? p : null;
        }

        public static string Normalize(string ext)
        {
            ext = (ext ?? "").Trim().ToLowerInvariant();
            if (ext.Length == 0) return ".txt";
            return ext.StartsWith('.') ? ext : "." + ext;
        }
    }
}
