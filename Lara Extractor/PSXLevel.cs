using System;
using System.Collections.Generic;

namespace Lara_Extractor
{
    /// <summary>
    /// A level of the PlayStation versions of Tomb Raider: The Last Revelation / Chronicles, as stored in a GAMEWAD.OBJ entry.
    ///
    /// The header is TOMB5's `struct Level` (GAME/SETUP.H). The level data does not start at the beginning of the
    /// GAMEWAD entry (a relocatable code module comes first), so the header is located by its signature:
    /// offsetSoundPointers = 0x800 and offsetSoundData = 0x1000, all offsets being relative to the header.
    ///
    /// Textures live in VRAM: two 512x256 16-bit blocks at offsetTextiles are uploaded to VRAM (512,0) and (512,256).
    /// Every texture is a quad of UVs plus a tpage (page, depth) and a CLUT (palette position), see PSXTEXTSTRUCT.
    /// </summary>
    public sealed class PSXLevel
    {
        public const int VramWidth = 1024;
        public const int VramHeight = 512;
        private const int TextileSize = 0x40000;
        private const int RoomInfoSize = 80;
        private const int OutsideRoomOffsetsSize = 1460;
        private const int TextureSize = 16;
        private const int MMTextureSize = 3 * TextureSize;

        /// <summary>One entry of psxtextinfo, or one of the three textures of an MMTEXTURE (room textures).</summary>
        public readonly record struct Texture(byte[] U, byte[] V, ushort Clut, ushort Tpage)
        {
            /// <summary>0 = 4-bit, 1 = 8-bit, 2 = 15-bit.</summary>
            public int ColorMode => (Tpage >> 7) & 3;
        }

        private readonly byte[] _data;
        private readonly ushort[] _vram = new ushort[VramWidth * VramHeight];

        public int Base { get; private set; }
        public int ObjectVersion { get; private set; }
        public int NumRooms { get; private set; }
        public int ClutStartY { get; private set; }
        public int NumLevelItems { get; private set; }

        public List<Texture> ObjectTextures { get; } = new();
        /// <summary>Room textures: three textures per MMTEXTURE, in file order (t[0], t[1], t[2] of entry 0, then entry 1, ...).</summary>
        public List<Texture> RoomTextures { get; } = new();
        public int RoomTextureSets => RoomTextures.Count / 3;

        private PSXLevel(byte[] data) => _data = data;

        private int I32(int offset) => BitConverter.ToInt32(_data, Base + offset);
        private ushort U16(int offset) => BitConverter.ToUInt16(_data, Base + offset);

        /// <summary>Returns null when the data does not contain a recognizable level header.</summary>
        public static PSXLevel? TryParse(byte[] data, out string error)
        {
            error = "";
            var level = new PSXLevel(data);
            if (!level.FindHeader())
            {
                error = "level header not found";
                return null;
            }

            try
            {
                level.ReadHeaderAndTables();
                level.BuildVram();
                return level;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is IndexOutOfRangeException || ex is ArgumentException)
            {
                error = ex.Message;
                return null;
            }
        }

        private bool FindHeader()
        {
            int limit = Math.Min(0x60000, _data.Length - 0xE4);
            for (int b = 0; b <= limit; b += 0x800)
            {
                int soundPointers = BitConverter.ToInt32(_data, b + 4);
                int soundData = BitConverter.ToInt32(_data, b + 8);
                int textiles = BitConverter.ToInt32(_data, b + 12);
                int animFrames = BitConverter.ToInt32(_data, b + 16);

                if (soundPointers == 0x800 && soundData == 0x1000 &&
                    textiles > soundData && (textiles % 0x800) == 0 && b + (long)animFrames <= _data.Length &&
                    animFrames - textiles == 2 * TextileSize)
                {
                    Base = b;
                    return true;
                }
            }
            return false;
        }

        private void ReadHeaderAndTables()
        {
            ObjectVersion = I32(0x00);
            int offsetTextiles = I32(0x0C);
            int offsetRoomInfo = I32(0x14);
            ClutStartY = U16(0x2C);
            NumRooms = U16(0x2E);
            NumLevelItems = U16(0x32);

            int roomInfoLength = I32(0x38);
            int floorDataLength = I32(0x3C);
            int outsideRoomTableLength = I32(0x40);
            int roomBoundingBoxesLength = I32(0x44);
            int meshBaseLength = I32(0x4C);
            int meshesLength = I32(0x50);
            int animsLength = I32(0x54);
            int changesLength = I32(0x58);
            int rangesLength = I32(0x5C);
            int commandsLength = I32(0x60);
            int bonesLength = I32(0x64);
            int textureInfoLength = I32(0x6C);
            int mmTextureInfoLength = I32(0x74);
            int animTextureRangesLength = I32(0x78);

            if (NumRooms <= 0 || NumRooms > 1000 || textureInfoLength % TextureSize != 0 || mmTextureInfoLength % MMTextureSize != 0)
                throw new InvalidOperationException("unexpected level header values");

            // Everything from the room table to the sample infos is a single blob (roomInfoLength bytes).
            // Walk it in the same order as LoadLevel() in TOMB5's GAME/SETUP.C.
            long p = (long)Base + offsetRoomInfo;
            long blobEnd = p + roomInfoLength;
            long cursor = p + (long)RoomInfoSize * NumRooms;

            for (int r = 0; r < NumRooms; r++)
            {
                long ri = p + (long)RoomInfoSize * r;
                // data, door, floor, light, mesh: sizes (they become pointers once loaded)
                for (int k = 0; k < 5; k++)
                    cursor += BitConverter.ToInt32(_data, (int)ri + k * 4);
            }

            cursor += floorDataLength;
            cursor += OutsideRoomOffsetsSize + outsideRoomTableLength;
            cursor += roomBoundingBoxesLength;
            cursor += meshBaseLength + meshesLength + animsLength + changesLength + rangesLength + commandsLength + bonesLength;
            cursor += animTextureRangesLength;

            long textureInfoOffset = cursor;
            long mmTextureOffset = textureInfoOffset + textureInfoLength + I32(0x70) /* spriteInfoLength */;

            if (textureInfoOffset + textureInfoLength > blobEnd || mmTextureOffset + mmTextureInfoLength > blobEnd || blobEnd > _data.Length)
                throw new InvalidOperationException("texture tables fall outside the level data");

            for (int i = 0; i < textureInfoLength / TextureSize; i++)
                ObjectTextures.Add(ReadTexture((int)textureInfoOffset + i * TextureSize));

            for (int i = 0; i < mmTextureInfoLength / TextureSize; i++)
                RoomTextures.Add(ReadTexture((int)mmTextureOffset + i * TextureSize));
        }

        private Texture ReadTexture(int offset)
        {
            // u0 v0 clut(2) | u1 v1 tpage(2) | u2 v2 pad(2) | u3 v3 pad(2)
            var u = new[] { _data[offset], _data[offset + 4], _data[offset + 8], _data[offset + 12] };
            var v = new[] { _data[offset + 1], _data[offset + 5], _data[offset + 9], _data[offset + 13] };
            ushort clut = BitConverter.ToUInt16(_data, offset + 2);
            ushort tpage = BitConverter.ToUInt16(_data, offset + 6);
            return new Texture(u, v, clut, tpage);
        }

        private void BuildVram()
        {
            int textileOffset = Base + I32(0x0C);
            if (textileOffset + 2 * TextileSize > _data.Length)
                throw new InvalidOperationException("textile data runs past the end of the level");

            // Two 512x256 blocks, uploaded at (512, 0) and (512, 256).
            for (int y = 0; y < 512; y++)
                for (int x = 0; x < 512; x++)
                    _vram[y * VramWidth + 512 + x] = BitConverter.ToUInt16(_data, textileOffset + (y * 512 + x) * 2);
        }

        private ushort Vram(int x, int y)
            => (x < 0 || y < 0 || x >= VramWidth || y >= VramHeight) ? (ushort)0 : _vram[y * VramWidth + x];

        private static void WriteColor(byte[] bgra, int index, ushort value)
        {
            if (value == 0)
            {
                bgra[index] = bgra[index + 1] = bgra[index + 2] = bgra[index + 3] = 0; // 0x0000 is transparent on PSX
                return;
            }
            bgra[index] = (byte)(((value >> 10) & 31) * 255 / 31);     // B
            bgra[index + 1] = (byte)(((value >> 5) & 31) * 255 / 31);  // G
            bgra[index + 2] = (byte)((value & 31) * 255 / 31);         // R
            bgra[index + 3] = 255;
        }

        /// <summary>Renders a texture to BGRA32 using its tpage/CLUT. Returns false if the rectangle is degenerate.</summary>
        public bool TryRender(Texture t, out int width, out int height, out byte[] bgra)
        {
            width = height = 0;
            bgra = Array.Empty<byte>();

            // A triangle leaves the fourth corner at (0,0); ignore it unless it is a real corner.
            bool ignoreFourth = t.U[3] == 0 && t.V[3] == 0 && (t.U[0] | t.V[0] | t.U[1] | t.V[1] | t.U[2] | t.V[2]) != 0
                                && Math.Min(Math.Min(t.U[0], t.U[1]), t.U[2]) > 0;
            int count = ignoreFourth ? 3 : 4;

            int u0 = 255, u1 = 0, v0 = 255, v1 = 0;
            for (int i = 0; i < count; i++)
            {
                u0 = Math.Min(u0, t.U[i]); u1 = Math.Max(u1, t.U[i]);
                v0 = Math.Min(v0, t.V[i]); v1 = Math.Max(v1, t.V[i]);
            }

            width = u1 - u0 + 1;
            height = v1 - v0 + 1;
            if (width < 1 || height < 1)
                return false;

            int pageX = (t.Tpage & 15) * 64;
            int pageY = ((t.Tpage >> 4) & 1) * 256;
            int clutX = (t.Clut & 63) * 16;
            int clutY = t.Clut >> 6;
            int mode = t.ColorMode;

            bgra = new byte[width * height * 4];

            ushort[]? palette = null;
            if (mode == 0)
            {
                palette = new ushort[16];
                for (int i = 0; i < 16; i++) palette[i] = Vram(clutX + i, clutY);
            }
            else if (mode == 1)
            {
                palette = new ushort[256];
                for (int i = 0; i < 256; i++) palette[i] = Vram(clutX + i, clutY);
            }

            for (int y = 0; y < height; y++)
            {
                int v = v0 + y;
                for (int x = 0; x < width; x++)
                {
                    int u = u0 + x;
                    ushort color;
                    switch (mode)
                    {
                        case 0: // 4-bit: 4 pixels per VRAM word
                            color = palette![(Vram(pageX + u / 4, pageY + v) >> ((u % 4) * 4)) & 15];
                            break;
                        case 1: // 8-bit: 2 pixels per VRAM word
                            color = palette![(Vram(pageX + u / 2, pageY + v) >> ((u % 2) * 8)) & 255];
                            break;
                        default: // 15-bit direct
                            color = Vram(pageX + u, pageY + v);
                            break;
                    }
                    WriteColor(bgra, (y * width + x) * 4, color);
                }
            }

            return true;
        }
    }
}
