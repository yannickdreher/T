using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace T.UI.Services;

/// <summary>
/// File icons and type names from the Windows shell - the same (colored) icons Windows
/// Explorer shows, including those of the apps registered for a file type. Remote files
/// have no local path, so the shell is asked by name only (SHGFI_USEFILEATTRIBUTES).
/// Must be called on the UI thread (the shell needs an STA thread).
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class ShellIcons
{
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeNormal = 0x80;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint ShgfiTypeName = 0x400;
    private const uint ShgfiSysIconIndex = 0x4000;
    private const int IldTransparent = 0x1;
    private const int IdoShgioiLink = 0x0FFFFFFE;

    // IImageList::GetIcon: 3 IUnknown methods + Add, ReplaceIcon, SetOverlayImage, Replace, AddMasked, Draw, Remove.
    private const int GetIconVtableSlot = 10;

    // SHIL_SMALL (16 px), SHIL_LARGE (32), SHIL_EXTRALARGE (48), SHIL_JUMBO (256)
    private static readonly (int Id, int Size)[] ImageLists = [(1, 16), (0, 32), (2, 48), (4, 256)];
    private static readonly nint[] ImageListHandles = new nint[ImageLists.Length];
    private static readonly Guid ImageListIid = new("46EB5926-582E-4017-9FDF-E8998DAA0950");
    private static int? _linkOverlay;

    /// <summary>System image list index and type name ("Text Document", "File folder") for a name.</summary>
    public static bool TryQuery(string name, bool isDirectory, out int iconIndex, out string typeName)
    {
        var info = default(ShFileInfo);
        var result = SHGetFileInfo(name, isDirectory ? FileAttributeDirectory : FileAttributeNormal, ref info,
            (uint)sizeof(ShFileInfo), ShgfiSysIconIndex | ShgfiTypeName | ShgfiUseFileAttributes);

        iconIndex = info.IconIndex;
        typeName = new string(info.TypeName);
        return result != 0;
    }

    /// <summary>The icon at <paramref name="iconIndex"/> with at least <paramref name="pixelSize"/> pixels (if available).</summary>
    public static Bitmap? GetIcon(int iconIndex, int pixelSize, bool linkOverlay)
    {
        int list = 0;
        while (list < ImageLists.Length - 1 && ImageLists[list].Size < pixelSize) list++;

        var bitmap = ExtractIcon(list, iconIndex, linkOverlay, out bool mostlyEmpty);

        // Types without 256 px artwork come as a small icon in the corner of a huge canvas.
        if (mostlyEmpty && list == ImageLists.Length - 1)
        {
            bitmap?.Dispose();
            bitmap = ExtractIcon(list - 1, iconIndex, linkOverlay, out _);
        }
        return bitmap;
    }

    private static Bitmap? ExtractIcon(int list, int iconIndex, bool linkOverlay, out bool mostlyEmpty)
    {
        mostlyEmpty = false;
        var imageList = GetImageList(list);
        if (imageList == 0) return null;

        int flags = IldTransparent;
        if (linkOverlay && LinkOverlay > 0)
            flags |= LinkOverlay << 8; // INDEXTOOVERLAYMASK

        nint icon = 0;
        var vtable = *(nint**)imageList;
        var getIcon = (delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)vtable[GetIconVtableSlot];
        if (getIcon(imageList, iconIndex, flags, &icon) < 0 || icon == 0)
            return null;

        try
        {
            return ToBitmap(icon, out mostlyEmpty);
        }
        finally
        {
            DestroyIcon(icon);
        }
    }

    private static int LinkOverlay => _linkOverlay ??= SHGetIconOverlayIndexW(0, IdoShgioiLink);

    private static nint GetImageList(int list)
    {
        if (ImageListHandles[list] == 0 && SHGetImageList(ImageLists[list].Id, in ImageListIid, out var handle) >= 0)
            ImageListHandles[list] = handle; // kept for the lifetime of the process
        return ImageListHandles[list];
    }

    private static Bitmap? ToBitmap(nint icon, out bool mostlyEmpty)
    {
        mostlyEmpty = false;
        if (!GetIconInfo(icon, out var iconInfo))
            return null;

        try
        {
            var bitmapObject = default(BitmapObject);
            if (iconInfo.Color == 0 || GetObjectW(iconInfo.Color, sizeof(BitmapObject), &bitmapObject) == 0)
                return null;

            int width = bitmapObject.Width, height = bitmapObject.Height;
            var pixels = ReadPixels(iconInfo.Color, width, height);
            if (pixels is null)
                return null;

            if (!HasAlpha(pixels))
            {
                // Icons without an alpha channel: transparency comes from the AND mask (white = transparent).
                var mask = ReadPixels(iconInfo.Mask, width, height);
                for (int i = 0; i < pixels.Length; i += 4)
                    pixels[i + 3] = mask is null || mask[i] == 0 ? (byte)255 : (byte)0;
            }

            mostlyEmpty = width >= 128 && ContentWidth(pixels, width, height) <= width / 4;

            fixed (byte* data = pixels)
            {
                return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul, (nint)data,
                    new PixelSize(width, height), new Vector(96, 96), width * 4);
            }
        }
        finally
        {
            if (iconInfo.Color != 0) DeleteObject(iconInfo.Color);
            if (iconInfo.Mask != 0) DeleteObject(iconInfo.Mask);
        }
    }

    /// <summary>Reads a bitmap as top-down 32 bpp BGRA.</summary>
    private static byte[]? ReadPixels(nint bitmap, int width, int height)
    {
        if (bitmap == 0) return null;

        var pixels = new byte[width * height * 4];
        var info = default(BitmapInfo);
        info.Header.Size = (uint)sizeof(BitmapInfoHeader);
        info.Header.Width = width;
        info.Header.Height = -height; // top-down
        info.Header.Planes = 1;
        info.Header.BitCount = 32;

        var dc = GetDC(0);
        try
        {
            fixed (byte* data = pixels)
                return GetDIBits(dc, bitmap, 0, (uint)height, data, &info, 0) == 0 ? null : pixels;
        }
        finally
        {
            _ = ReleaseDC(0, dc);
        }
    }

    private static bool HasAlpha(byte[] pixels)
    {
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) return true;
        return false;
    }

    /// <summary>Width of the non-transparent area.</summary>
    private static int ContentWidth(byte[] pixels, int width, int height)
    {
        int min = width, max = -1;
        for (int y = 0; y < height; y++)
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                if (pixels[row + (x * 4) + 3] == 0) continue;
                if (x < min) min = x;
                if (x > max) max = x;
            }
        }
        return max < min ? 0 : max - min + 1;
    }

    // ── Win32 ────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        public fixed char DisplayName[260];
        public fixed char TypeName[80];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public int HotspotX;
        public int HotspotY;
        public nint Mask;
        public nint Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapObject
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public fixed uint Colors[256]; // room for a color table GetDIBits may write
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SHGetFileInfo(string path, uint fileAttributes, ref ShFileInfo info, uint size, uint flags);

    [LibraryImport("shell32.dll", EntryPoint = "#727")]
    private static partial int SHGetImageList(int imageList, in Guid iid, out nint imageListHandle);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetIconOverlayIndexW(nint iconPath, int iconIndex);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetIconInfo(nint icon, out IconInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial int GetObjectW(nint handle, int size, void* buffer);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, BitmapInfo* info, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);
}
