using System.Diagnostics;
using System.Text.Json;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json;
using SkiaSharp;

namespace FModelCli;

internal static class Program
{
    private static Config _cfg = null!;
    private static DefaultFileProvider _provider = null!;

    private static readonly System.Text.Json.JsonSerializerOptions StdoutJson = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static int Main(string[] args)
    {
        try
        {
            _cfg = Config.Load();

            if (args.Length == 0) return Emit(new { ok = false, error = "no subcommand. expected: status|search|read|inspect|export-tex|export-mesh|export-raw|list" }, exit: 2);

            var cmd = args[0];
            var rest = args[1..];

            // `list` doesn't need provider
            if (cmd == "list") return CmdList(rest);

            InitProvider();

            return cmd switch
            {
                "status" => CmdStatus(),
                "search" => CmdSearch(rest),
                "read" => CmdRead(rest),
                "inspect" => CmdInspect(rest),
                "export-tex" => CmdExportTexture(rest),
                "export-mesh" => CmdExportMesh(rest),
                "export-raw" => CmdExportRaw(rest),
                _ => Emit(new { ok = false, error = $"unknown subcommand: {cmd}" }, exit: 2),
            };
        }
        catch (Exception e)
        {
            return Emit(new
            {
                ok = false,
                error = e.Message,
                type = e.GetType().Name,
                stack = e.StackTrace,
            }, exit: 1);
        }
    }

    // --- Provider init ----------------------------------------------------

    private static void InitProvider()
    {
        EnsureOodle();

        var versionContainer = new VersionContainer(ParseGame(_cfg.UeVersion));
        _provider = new DefaultFileProvider(_cfg.PaksDir, SearchOption.AllDirectories, versionContainer, StringComparer.OrdinalIgnoreCase);
        _provider.Initialize();

        var key = _cfg.AesKey?.Trim() ?? "";
        if (key.Length > 0)
        {
            _provider.SubmitKey(new FGuid(), new FAesKey(key));
        }
        else
        {
            // Submit empty key under the zero GUID so any unencrypted volumes mount cleanly.
            _provider.SubmitKey(new FGuid(), new FAesKey(new byte[32]));
        }

        if (!string.IsNullOrEmpty(_cfg.MappingsFile) && File.Exists(_cfg.MappingsFile))
        {
            _provider.MappingsContainer = new FileUsmapTypeMappingsProvider(_cfg.MappingsFile);
        }

        _provider.PostMount();
        _provider.LoadVirtualPaths();
    }

    private static void EnsureOodle()
    {
        // CUE4Parse-Natives ships the Oodle bridge but the runtime DLL itself
        // is downloaded on demand. Drop it next to the executable so subsequent
        // runs find it without re-downloading.
        var dllName = ResolveOodleDllName();
        var oodlePath = Path.Combine(AppContext.BaseDirectory, dllName);
        if (!File.Exists(oodlePath))
        {
            if (!OodleHelper.DownloadOodleDll())
                throw new Exception($"failed to download Oodle DLL ({dllName})");
            // CUE4Parse drops it in the working dir. Move it next to the binary if needed.
            var fallback = Path.Combine(Directory.GetCurrentDirectory(), dllName);
            if (!File.Exists(oodlePath) && File.Exists(fallback))
                File.Move(fallback, oodlePath);
        }
        OodleHelper.Initialize(oodlePath);
    }

    private static string ResolveOodleDllName()
    {
        // Try the OODLE_DLL_NAME constant if the version exposes it; fall back to known UE5 name.
        var f = typeof(OodleHelper).GetField("OODLE_DLL_NAME", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (f?.GetValue(null) is string name && !string.IsNullOrEmpty(name)) return name;
        return OperatingSystem.IsWindows() ? "oo2core_9_win64.dll" : "liboo2corelinux64.so.9";
    }

    private static EGame ParseGame(string s) => s switch
    {
        "GAME_UE5_4" => EGame.GAME_UE5_4,
        "GAME_UE5_3" => EGame.GAME_UE5_3,
        "GAME_UE5_2" => EGame.GAME_UE5_2,
        "GAME_UE5_1" => EGame.GAME_UE5_1,
        "GAME_UE5_0" => EGame.GAME_UE5_0,
        "GAME_UE4_27" => EGame.GAME_UE4_27,
        _ => Enum.TryParse<EGame>(s, out var g) ? g : throw new ArgumentException($"unknown UE version: {s}"),
    };

    // --- Commands ---------------------------------------------------------

    private static int CmdStatus()
    {
        return Emit(new
        {
            ok = true,
            paksDir = _cfg.PaksDir,
            outputDir = _cfg.OutputDir,
            ueVersion = _cfg.UeVersion,
            mappings = _cfg.MappingsFile,
            mappingsLoaded = _provider.MappingsContainer != null,
            files = _provider.Files.Count,
        });
    }

    private static int CmdSearch(string[] args)
    {
        if (args.Length < 1) return Emit(new { ok = false, error = "search: missing pattern" }, exit: 2);
        var pattern = args[0];
        var limit = args.Length > 1 && int.TryParse(args[1], out var l) ? l : 200;

        var matches = new List<string>(limit);
        var matcher = GlobToRegex(pattern);
        foreach (var key in _provider.Files.Keys)
        {
            if (matcher.IsMatch(key))
            {
                matches.Add(key);
                if (matches.Count >= limit) break;
            }
        }

        return Emit(new { ok = true, pattern, count = matches.Count, truncated = matches.Count == limit, matches });
    }

    private static int CmdRead(string[] args)
    {
        if (args.Length < 1) return Emit(new { ok = false, error = "read: missing path" }, exit: 2);
        var path = NormalizePath(args[0]);

        var allObjects = _provider.LoadPackage(path).GetExports().ToArray();
        var json = JsonConvert.SerializeObject(allObjects, Newtonsoft.Json.Formatting.None);
        return EmitRaw($"{{\"ok\":true,\"path\":\"{EscapeJson(path)}\",\"objects\":{json}}}");
    }

    private static int CmdInspect(string[] args)
    {
        if (args.Length < 1) return Emit(new { ok = false, error = "inspect: missing path" }, exit: 2);
        var path = NormalizePath(args[0]);

        var objects = _provider.LoadPackage(path).GetExports().ToArray();
        var summary = new List<object>();
        foreach (var obj in objects)
        {
            var props = obj.Properties;
            string? parent = null;
            var textures = new List<object>();
            var scalars = new List<object>();
            var vectors = new List<object>();
            string? blendMode = null;
            bool? twoSided = null;
            float? opacityClip = null;

            // The serialized form is the same JObject FModel shows. Easiest: serialize the object,
            // re-parse, and pluck what's useful.
            var json = JsonConvert.SerializeObject(obj);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("Properties", out var p))
                {
                    if (p.TryGetProperty("Parent", out var par) && par.TryGetProperty("ObjectPath", out var op))
                        parent = op.GetString();
                    if (p.TryGetProperty("TextureParameterValues", out var tpvs))
                        foreach (var t in tpvs.EnumerateArray()) textures.Add(SimplifyParam(t, "texture"));
                    if (p.TryGetProperty("ScalarParameterValues", out var spvs))
                        foreach (var s in spvs.EnumerateArray()) scalars.Add(SimplifyParam(s, "scalar"));
                    if (p.TryGetProperty("VectorParameterValues", out var vpvs))
                        foreach (var v in vpvs.EnumerateArray()) vectors.Add(SimplifyParam(v, "vector"));
                    if (p.TryGetProperty("BasePropertyOverrides", out var bpo))
                    {
                        if (bpo.TryGetProperty("BlendMode", out var bm)) blendMode = bm.GetString();
                        if (bpo.TryGetProperty("TwoSided", out var ts)) twoSided = ts.GetBoolean();
                        if (bpo.TryGetProperty("OpacityMaskClipValue", out var omc)) opacityClip = omc.GetSingle();
                    }
                }
            }
            catch { /* best-effort */ }

            summary.Add(new
            {
                name = obj.Name,
                type = obj.GetType().Name,
                exportType = obj.ExportType,
                parent,
                blendMode,
                twoSided,
                opacityMaskClipValue = opacityClip,
                textures,
                scalars,
                vectors,
            });
        }

        return Emit(new { ok = true, path, objects = summary });
    }

    private static object SimplifyParam(System.Text.Json.JsonElement entry, string kind)
    {
        string? name = null;
        if (entry.TryGetProperty("ParameterInfo", out var pi) && pi.TryGetProperty("Name", out var n))
            name = n.GetString();

        if (kind == "texture" && entry.TryGetProperty("ParameterValue", out var tv))
        {
            string? objName = null, objPath = null;
            if (tv.TryGetProperty("ObjectName", out var on)) objName = on.GetString();
            if (tv.TryGetProperty("ObjectPath", out var op)) objPath = op.GetString();
            return new { name, objectName = objName, objectPath = objPath };
        }
        if (kind == "scalar" && entry.TryGetProperty("ParameterValue", out var sv))
        {
            return new { name, value = sv.GetSingle() };
        }
        if (kind == "vector" && entry.TryGetProperty("ParameterValue", out var vv))
        {
            float r = 0, g = 0, b = 0, a = 0;
            if (vv.TryGetProperty("R", out var rE)) r = rE.GetSingle();
            if (vv.TryGetProperty("G", out var gE)) g = gE.GetSingle();
            if (vv.TryGetProperty("B", out var bE)) b = bE.GetSingle();
            if (vv.TryGetProperty("A", out var aE)) a = aE.GetSingle();
            return new { name, r, g, b, a };
        }
        return new { name };
    }

    private static int CmdExportTexture(string[] args)
    {
        if (args.Length < 1) return Emit(new { ok = false, error = "export-tex: missing path" }, exit: 2);
        var path = NormalizePath(args[0]);

        var tex = _provider.LoadPackageObject<UTexture2D>(path);
        var bitmap = tex.Decode() ?? throw new Exception("texture decode returned null");
        var outPath = ResolveOutputPath(path, ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using (var img = SKImage.FromBitmap(bitmap))
        using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
        using (var fs = File.OpenWrite(outPath))
            data.SaveTo(fs);

        return Emit(new
        {
            ok = true,
            path,
            outputPath = outPath,
            width = bitmap.Width,
            height = bitmap.Height,
            format = "png",
        });
    }

    private static int CmdExportMesh(string[] args)
    {
        if (args.Length < 1) return Emit(new { ok = false, error = "export-mesh: missing path" }, exit: 2);
        var path = NormalizePath(args[0]);

        var mesh = _provider.LoadPackage(path).GetExports()
            .FirstOrDefault(o => o.GetType().Name.Contains("SkeletalMesh") || o.GetType().Name.Contains("StaticMesh"))
            ?? throw new Exception("no SkeletalMesh / StaticMesh export found in package");

        var options = new ExporterOptions
        {
            MeshFormat = EMeshFormat.ActorX,
            TextureFormat = ETextureFormat.Png,
            ExportMorphTargets = false,
        };
        var exporter = new Exporter(mesh, options);
        var outDir = Path.GetDirectoryName(ResolveOutputPath(path, ".psk"))!;
        Directory.CreateDirectory(outDir);

        if (!exporter.TryWriteToDir(new DirectoryInfo(outDir), out var label, out var savedPath))
            throw new Exception("mesh exporter wrote nothing");

        return Emit(new { ok = true, path, outputPath = savedPath, label });
    }

    private static int CmdExportRaw(string[] args)
    {
        if (args.Length < 1) return Emit(new { ok = false, error = "export-raw: missing path" }, exit: 2);
        var path = NormalizePath(args[0]);

        var allObjects = _provider.LoadPackage(path).GetExports().ToArray();
        var json = JsonConvert.SerializeObject(allObjects, Newtonsoft.Json.Formatting.Indented);

        var outPath = ResolveOutputPath(path, ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, json);

        return Emit(new { ok = true, path, outputPath = outPath, bytes = json.Length });
    }

    private static int CmdList(string[] args)
    {
        var prefix = args.Length > 0 ? args[0] : "";
        var root = _cfg.OutputDir;
        if (!Directory.Exists(root)) return Emit(new { ok = true, root, files = Array.Empty<string>() });

        var search = string.IsNullOrEmpty(prefix) ? root : Path.Combine(root, prefix.Replace('/', Path.DirectorySeparatorChar));
        var startDir = Directory.Exists(search) ? search : (Path.GetDirectoryName(search) ?? root);
        var pattern = Directory.Exists(search) ? "*" : Path.GetFileName(search) + "*";

        if (!Directory.Exists(startDir)) return Emit(new { ok = true, root, files = Array.Empty<string>() });

        var files = Directory.EnumerateFileSystemEntries(startDir, pattern, SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Take(500)
            .ToArray();
        return Emit(new { ok = true, root, prefix, count = files.Length, files });
    }

    // --- Helpers ---------------------------------------------------------

    private static string NormalizePath(string raw)
    {
        // Accept any of these:
        //   "Sandfall/Content/.../Asset"
        //   "Sandfall/Content/.../Asset.uasset"
        //   "/Game/.../Asset"
        //   "Sandfall/Content/.../Asset.0"   (FModel-style ObjectPath)
        var p = raw.Replace('\\', '/');

        // Strip ".0" / ".1" trailing index from FModel ObjectPath form.
        var lastDot = p.LastIndexOf('.');
        if (lastDot > 0 && int.TryParse(p[(lastDot + 1)..], out _))
            p = p[..lastDot];

        // Strip extensions CUE4Parse keeps in Files keys.
        foreach (var ext in new[] { ".uasset", ".umap", ".uexp", ".ubulk" })
            if (p.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                p = p[..^ext.Length];

        // /Game/X.Y -> Sandfall/Content/X (for E33 specifically; package paths use mount point).
        if (p.StartsWith("/Game/"))
            p = "Sandfall/Content/" + p["/Game/".Length..];

        return p;
    }

    private static string ResolveOutputPath(string packagePath, string extension)
    {
        var relative = packagePath.Replace('/', Path.DirectorySeparatorChar);
        var basePath = Path.Combine(_cfg.OutputDir, relative);
        return Path.ChangeExtension(basePath, extension);
    }

    private static System.Text.RegularExpressions.Regex GlobToRegex(string pattern)
    {
        var sb = new System.Text.StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++;
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?') sb.Append("[^/]");
            else if ("\\.+()|[]{}^$".Contains(c)) sb.Append('\\').Append(c);
            else sb.Append(c);
        }
        sb.Append("$");
        return new System.Text.RegularExpressions.Regex(sb.ToString(), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static int Emit(object payload, int exit = 0)
    {
        Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(payload, StdoutJson));
        return exit;
    }

    private static int EmitRaw(string json)
    {
        Console.Out.WriteLine(json);
        return 0;
    }
}

internal sealed record Config(
    string PaksDir,
    string OutputDir,
    string UeVersion,
    string? MappingsFile,
    string? AesKey
)
{
    public static Config Load()
    {
        // 1) FMODEL_MCP_CONFIG env var  2) ./config.json next to the CLI  3) hardcoded E33 defaults.
        var envPath = Environment.GetEnvironmentVariable("FMODEL_MCP_CONFIG");
        var localPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        var repoPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "config.json");

        foreach (var p in new[] { envPath, localPath, repoPath })
        {
            if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;
            var json = File.ReadAllText(p);
            var cfg = System.Text.Json.JsonSerializer.Deserialize<Config>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (cfg != null) return cfg;
        }

        return new Config(
            PaksDir: @"D:\SteamLibrary\steamapps\common\Expedition 33\Sandfall\Content\Paks",
            OutputDir: @"D:\vivify_repo\Output\Exports",
            UeVersion: "GAME_UE5_4",
            MappingsFile: @"D:\vivify_repo\fmodel-mcp\mappings\Expedition33Mappings-1.5.4.usmap",
            AesKey: null
        );
    }
}
