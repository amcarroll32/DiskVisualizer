using System.IO;

namespace DiskVisualizer.Model;

/// <summary>
/// Eight file categories, one per categorical palette slot; everything else is Other.
/// Order matches the palette order in <see cref="Treemap.Theme"/>.
/// </summary>
public enum FileCategory
{
    Video,
    Image,
    Audio,
    Document,
    Archive,
    Program,
    DiskImage,
    Data,
    Other,
}

public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> Map = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, FileCategory>.AlternateLookup<ReadOnlySpan<char>> Lookup;

    static FileCategories()
    {
        Add(FileCategory.Video, ".mp4 .mkv .avi .mov .wmv .webm .m4v .flv .mpg .mpeg .m2ts .mts .ts .vob .3gp .rmvb .bk2 .bik .usm .ivf");
        Add(FileCategory.Image, ".jpg .jpeg .png .gif .bmp .tif .tiff .webp .heic .heif .avif .raw .cr2 .cr3 .nef .arw .dng .orf .rw2 .psd .psb .xcf .svg .ico .exr .hdr .tga .dds");
        Add(FileCategory.Audio, ".mp3 .flac .wav .aac .ogg .m4a .wma .opus .aiff .aif .alac .ape .mid .midi .wv .bank .fsb .wem .bnk .xwm .xwb .snd");
        Add(FileCategory.Document, ".pdf .doc .docx .xls .xlsx .xlsm .ppt .pptx .txt .rtf .odt .ods .odp .md .csv .epub .mobi .one .pst .ost .msg .eml .vsdx .pub .xps");
        Add(FileCategory.Archive, ".zip .rar .7z .tar .gz .tgz .bz2 .xz .zst .lz .lzma .cab .arj .z .apk .xapk .nupkg .jar .whl");
        Add(FileCategory.Program, ".exe .dll .sys .msi .msp .msu .ocx .drv .mui .efi .appx .appxbundle .msix .msixbundle .so .dylib .cpl .scr .com .winmd .node .pyd");
        Add(FileCategory.DiskImage, ".iso .img .vhd .vhdx .avhdx .vmdk .vdi .qcow2 .wim .esd .swm .dmg .vmem .vmsn .vsv .gho .tib");
        Add(FileCategory.Data, ".db .sqlite .sqlite3 .mdb .accdb .mdf .ldf .ndf .edb .ldb .dat .bin .cache .tmp .temp .log .etl .evtx .pak .vpk .bsa .ba2 .assets .bundle .resource .resources .unity3d .uasset .ubulk .forge .big .wad .gcf .ucas .utoc .pdb .obj .lib .pch .ipch .idb .ilk .blob .pack .idx .chk .bak .old .dmp .hprof .vdf .acf .sav .ucas .archive .resS .meg .tpac .gtp .vpp_pc .blp .ck2 .oppc .mix .fbq .pkg .entities .body .pck .rpf .cas .cat .toc .sb .wpk .xp3 .arc .ff .forge .nvph .safetensors .ckpt .gguf .onnx .pt .pth .h5 .tflite .npy .parquet .vmap");
        Lookup = Map.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    private static void Add(FileCategory category, string extensions)
    {
        foreach (var ext in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            Map.TryAdd(ext, category);
    }

    public static FileCategory FromFileName(ReadOnlySpan<char> fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (ext.Length <= 1)
            return FileCategory.Other;
        if (Lookup.TryGetValue(ext, out var category))
            return category;
        // Numbered chunks (data.000, archive.m00) are almost always game or app data.
        return char.IsAsciiDigit(ext[^1]) && char.IsAsciiDigit(ext[^2]) ? FileCategory.Data : FileCategory.Other;
    }

    public static string DisplayName(FileCategory category) => category switch
    {
        FileCategory.Video => "Video",
        FileCategory.Image => "Images",
        FileCategory.Audio => "Audio",
        FileCategory.Document => "Documents",
        FileCategory.Archive => "Archives",
        FileCategory.Program => "Programs & system",
        FileCategory.DiskImage => "Disk images & VMs",
        FileCategory.Data => "App & game data, caches",
        _ => "Other files",
    };
}
