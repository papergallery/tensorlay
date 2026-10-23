using System.IO;
using TensorLay.Models;

namespace TensorLay.Services;

// Single definition of "which files are models" and "where a service keeps
// them". The Models page scan, the inventory pushed to the relay and model
// deletion all go through here — if they disagreed, the agent would be
// offered paths the desktop then refuses to delete (or the other way round).
public static class ModelFiles
{
    // Mirrors DELETABLE_EXTENSIONS in relay.py. .pth covers ESRGAN upscalers,
    // .bin some CLIP/embedding files, .onnx the insightface detectors, .gguf
    // quantized LLM weights.
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".safetensors", ".sft", ".ckpt", ".pt", ".pth", ".bin", ".gguf", ".onnx",
    };

    private const int MaxRelativePathLength = 400;
    private const int MaxSegments = 8;

    public static bool HasModelExtension(string path) => Extensions.Contains(Path.GetExtension(path));

    // Absolute folder the service keeps its models under, or null when the
    // service has no on-disk models we manage (Ollama, Whisper, F5-TTS, ...).
    // ModelsScanRoot is the parent of all model-type folders (ComfyUI's
    // "models" holding checkpoints/, loras/, vae/...); services with a flat
    // layout only set ModelsSubfolder.
    public static string? GetModelsRoot(ServiceDefinition def, string installDir)
    {
        if (string.IsNullOrEmpty(def.ModelsSubfolder)) return null;
        string rel = string.IsNullOrEmpty(def.ModelsScanRoot) ? def.ModelsSubfolder : def.ModelsScanRoot;
        string combined = string.IsNullOrEmpty(def.RelativeInstallPath)
            ? Path.Combine(installDir, rel)
            : Path.Combine(installDir, def.RelativeInstallPath, rel);
        return Path.GetFullPath(combined);
    }

    // Every model file under the service's models root. One recursive walk
    // with an extension filter (the previous per-pattern GetFiles walked the
    // tree five times). Unreadable folders are skipped rather than failing
    // the whole scan.
    public static List<ModelInfo> Scan(ServiceDefinition def, string installDir)
    {
        string? root = GetModelsRoot(def, installDir);
        if (root is null || !Directory.Exists(root)) return new();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        };
        var result = new List<ModelInfo>();
        foreach (string path in Directory.EnumerateFiles(root, "*", options))
        {
            if (!HasModelExtension(path)) continue;
            try
            {
                var info = new FileInfo(path);
                result.Add(new ModelInfo
                {
                    ServiceId = def.Id,
                    FileName = info.Name,
                    FullPath = path,
                    RelativePath = Path.GetRelativePath(root, path).Replace('\\', '/'),
                    SizeBytes = info.Length,
                    ModifiedUtc = info.LastWriteTimeUtc,
                });
            }
            catch (IOException) { /* vanished mid-scan */ }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    // Maps a relay-supplied relative path ("loras/foo.safetensors") onto the
    // service's models root. Returns null with a human-readable reason when
    // the path is rejected. The format rules mirror relay._validate_delete_item
    // so a rejection explains itself; the containment check at the end is the
    // one that actually guarantees nothing outside the models folder is ever
    // touched — the desktop owns the disk and does not trust the relay's
    // validation.
    public static string? ResolveModelPath(ServiceDefinition def, string installDir, string relativePath, out string error)
    {
        error = "";
        string? root = GetModelsRoot(def, installDir);
        if (root is null)
        {
            error = "this service keeps no model files on disk";
            return null;
        }
        if (string.IsNullOrEmpty(relativePath) || relativePath.Length > MaxRelativePathLength)
        {
            error = "empty or overly long path";
            return null;
        }
        if (relativePath.StartsWith('/') || relativePath.EndsWith('/'))
        {
            error = "leading or trailing slash";
            return null;
        }
        foreach (char c in relativePath)
        {
            if (c < 32 || "\\:*?\"<>|".Contains(c))
            {
                error = "path contains a backslash, colon, wildcard or control character";
                return null;
            }
        }
        string[] segments = relativePath.Split('/');
        if (segments.Length > MaxSegments)
        {
            error = "path is nested too deeply";
            return null;
        }
        foreach (string seg in segments)
        {
            // Windows strips trailing dots/spaces, so "loras." would alias "loras".
            if (seg.Length == 0 || seg == "." || seg == ".." || seg != seg.Trim() || seg.EndsWith('.'))
            {
                error = $"bad path segment '{seg}'";
                return null;
            }
        }
        if (!HasModelExtension(relativePath))
        {
            error = "not a model file type";
            return null;
        }

        string full = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        string rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            error = "path points outside the models folder";
            return null;
        }

        // The string check above can't see links: a junction or symlink below
        // the models root would make File.Delete act on whatever it points to.
        // The root itself is trusted — moving a whole models folder to another
        // drive with a junction is a common, deliberate setup — but every
        // component under it, and the file, must be a real directory/file.
        string current = root;
        foreach (string seg in segments)
        {
            current = Path.Combine(current, seg);
            try
            {
                if (!Path.Exists(current)) break;  // missing — nothing to follow
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    error = "the file sits behind a folder link (junction/symlink) - delete it manually";
                    return null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                return null;
            }
        }
        return full;
    }

    public static string FormatBytes(long bytes)
    {
        const double gb = 1024d * 1024 * 1024;
        const double mb = 1024d * 1024;
        if (bytes >= gb) return $"{bytes / gb:F2} GB";
        if (bytes >= mb) return $"{bytes / mb:F0} MB";
        return $"{bytes / 1024d:F0} KB";
    }
}
