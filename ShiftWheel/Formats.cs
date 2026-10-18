using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ShiftWheel;

internal enum FileKind { Unknown, Image, Video, Audio, Pdf }

internal static class Formats
{
    static readonly HashSet<string> Img = new(StringComparer.OrdinalIgnoreCase)
        { "png", "jpg", "jpeg", "jfif", "webp", "bmp", "gif", "tif", "tiff", "ico", "avif" };
    static readonly HashSet<string> Vid = new(StringComparer.OrdinalIgnoreCase)
        { "mp4", "mkv", "mov", "avi", "webm", "wmv", "flv", "m4v", "mpg", "mpeg", "3gp", "ts" };
    static readonly HashSet<string> Aud = new(StringComparer.OrdinalIgnoreCase)
        { "mp3", "wav", "flac", "ogg", "m4a", "aac", "opus", "wma", "aiff" };

    public static string Ext(string path) => Normalize(Path.GetExtension(path).TrimStart('.'));

    public static string Normalize(string ext)
    {
        ext = ext.ToLowerInvariant();
        return ext switch { "jpeg" or "jfif" => "jpg", "tif" => "tiff", _ => ext };
    }

    public static FileKind Classify(string path)
    {
        string ext = Path.GetExtension(path).TrimStart('.');
        if (ext.Equals("pdf", StringComparison.OrdinalIgnoreCase)) return FileKind.Pdf;
        if (Img.Contains(ext)) return FileKind.Image;
        if (Vid.Contains(ext)) return FileKind.Video;
        if (Aud.Contains(ext)) return FileKind.Audio;
        return FileKind.Unknown;
    }

    /// <summary>Output formats for the outer ring of the wheel.</summary>
    public static IReadOnlyList<string> Targets(FileKind kind, string ext)
    {
        ext = Normalize(ext);
        string[] all = kind switch
        {
            FileKind.Image => ext == "gif"
                ? new[] { "PNG", "JPG", "WEBP", "MP4", "PDF", "TXT", "DOCX", "BMP" }
                : new[] { "PNG", "JPG", "WEBP", "PDF", "TXT", "DOCX", "GIF", "ICO", "BMP", "TIFF" },
            FileKind.Video => new[] { "MP4", "MKV", "WEBM", "MOV", "AVI", "GIF", "MP3", "WAV", "M4A" },
            FileKind.Audio => new[] { "MP3", "WAV", "FLAC", "OGG", "M4A", "AAC", "OPUS" },
            FileKind.Pdf => new[] { "PNG", "JPG", "WEBP", "BMP", "TXT", "DOCX" },
            _ => Array.Empty<string>(),
        };
        return all.Where(t => !t.Equals(ext, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Tools for the inner ring: (id, label).</summary>
    public static IReadOnlyList<(string Id, string Label)> Tools(FileKind kind) => kind switch
    {
        FileKind.Image or FileKind.Video => new[]
        {
            ("compress", "Сжать"), ("strip", "Очистить"), ("resize", "50%"), ("rotate", "Повернуть"),
        },
        FileKind.Audio => new[] { ("compress", "Сжать"), ("strip", "Очистить") },
        _ => Array.Empty<(string, string)>(),
    };
}
