namespace FileOp.Core.Storage;

public static class StorageFileCategoryClassifier
{
    public static StorageFileCategory Classify(string? extension)
    {
        var normalized = NormalizeExtension(extension);
        if (normalized.Length == 0)
        {
            return StorageFileCategory.NoExtension;
        }

        return normalized switch
        {
            "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or
            "odt" or "ods" or "odp" or "rtf" or "txt" or "md" or "markdown" or
            "csv" or "tsv" or "epub" => StorageFileCategory.Documents,

            "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "tif" or
            "tiff" or "heic" or "heif" or "svg" or "ico" or "dng" or "raw" or
            "cr2" or "cr3" or "nef" or "arw" => StorageFileCategory.Images,

            "mp4" or "mkv" or "mov" or "avi" or "wmv" or "webm" or "m4v" or
            "mpeg" or "mpg" or "ts" or "mts" or "m2ts" => StorageFileCategory.Video,

            "mp3" or "flac" or "wav" or "aac" or "m4a" or "ogg" or "opus" or
            "wma" or "aiff" or "aif" => StorageFileCategory.Audio,

            "zip" or "7z" or "rar" or "tar" or "gz" or "bz2" or "xz" or "zst" or
            "cab" or "tgz" or "tbz" or "tbz2" => StorageFileCategory.Archives,

            "exe" or "dll" or "msi" or "msix" or "appx" or "appxbundle" or
            "msixbundle" or "sys" or "com" or "scr" => StorageFileCategory.Applications,

            "cs" or "c" or "cc" or "cpp" or "cxx" or "h" or "hh" or "hpp" or
            "hxx" or "rs" or "go" or "java" or "kt" or "kts" or "js" or "jsx" or
            "ts" or "tsx" or "py" or "rb" or "php" or "swift" or "sh" or "ps1" or
            "bat" or "cmd" or "sql" or "html" or "htm" or "css" or "scss" or
            "less" or "xml" or "json" or "yaml" or "yml" or "toml" or "ini" or
            "props" or "targets" => StorageFileCategory.Code,

            "db" or "sqlite" or "sqlite3" or "mdb" or "accdb" or "parquet" or
            "avro" or "orc" or "dat" or "log" or "bin" => StorageFileCategory.Data,

            "iso" or "vhd" or "vhdx" or "vmdk" or "wim" or "esd" or "img" or
            "dmg" => StorageFileCategory.DiskImages,

            "ttf" or "otf" or "woff" or "woff2" or "eot" => StorageFileCategory.Fonts,

            _ => StorageFileCategory.Other,
        };
    }

    public static string NormalizeExtension(string? extension) =>
        string.IsNullOrWhiteSpace(extension)
            ? string.Empty
            : extension.Trim().TrimStart('.').ToLowerInvariant();
}
