using System;
using System.IO;
using System.Collections.Generic;

var file = @"C:\Users\Checkm8ra1n\Documents\level10b.phd";
var b = File.ReadAllBytes(file);
int pos = 0;

uint R32() { uint v = BitConverter.ToUInt32(b, pos); pos += 4; return v; }
int  R16() { int  v = BitConverter.ToUInt16(b, pos); pos += 2; return v; }

R32(); int nt = (int)R32(); pos += nt * 65536; R32();
int nr = R16();
for (int r = 0; r < nr; r++) {
    pos += 16; int dw = (int)R32(); pos += dw * 2;
    int nP = R16(); pos += nP * 32;
    int nZ = R16(), nX = R16(); pos += nZ * nX * 8;
    pos += 2; int nL = R16(); pos += nL * 18; int nM = R16(); pos += nM * 18; pos += 4;
}
int nFD = (int)R32(); pos += nFD * 2;
int nMD = (int)R32(); int meshDataStart = pos; pos += nMD * 2;
int nMP = (int)R32(); int mpStart = pos; pos += nMP * 4;
int nA = (int)R32(); pos += nA * 32;
int nSC = (int)R32(); pos += nSC * 6;
int nAD = (int)R32(); pos += nAD * 8;
int nAC = (int)R32(); pos += nAC * 2;
int nMT = (int)R32(); pos += nMT * 4;
int nFr = (int)R32(); pos += nFr * 2;
int nMo = (int)R32(); pos += nMo * 18;
int nStat = (int)R32(); pos += nStat * 32;
int nOT = (int)R32(); int otStart = pos;

int firstRiff = -1;
for (int i = 4; i < b.Length - 4; i++)
    if (b[i]=='R' && b[i+1]=='I' && b[i+2]=='F' && b[i+3]=='F') { firstRiff = i; break; }
int palOffset = -1;
for (int i = firstRiff - 768; i >= Math.Max(0, firstRiff - 9000); i--) {
    bool valid = true; for (int j = 0; j < 768; j++) if (b[i+j] > 63) { valid = false; break; }
    if (valid) { palOffset = i; break; }
}
var pal = new byte[768];
Buffer.BlockCopy(b, palOffset, pal, 0, 768);
for (int i = 0; i < 768; i++) pal[i] = (byte)(pal[i] * 4);

int tilesStart = 8;

// Cerca quale mesh usa OT che campionano colori DORATI (R>150, G>120, B<80)
Console.WriteLine("Mesh con texture dorate (R>150 G>120 B<80):");
for (int m = 0; m < 15; m++)
{
    uint ptr = BitConverter.ToUInt32(b, mpStart + m * 4);
    int mOff = meshDataStart + (int)ptr;
    short nV = BitConverter.ToInt16(b, mOff+10);
    int vEnd = mOff + 12 + nV * 6;
    short nN = BitConverter.ToInt16(b, vEnd);
    int nEnd = nN > 0 ? vEnd+2+nN*6 : vEnd+2+Math.Abs((int)nN)*2;
    short nTQ = BitConverter.ToInt16(b, nEnd);
    int tqEnd = nEnd + 2 + nTQ * 10;
    short nTT = BitConverter.ToInt16(b, tqEnd);
    int ttEnd = tqEnd + 2 + nTT * 8;

    var goldPolys = new List<string>();
    // Check TQ
    for (int i = 0; i < nTQ; i++) {
        int ti = BitConverter.ToUInt16(b, nEnd+2+i*10+8) & 0x7FFF;
        int otOff = otStart+ti*20;
        int tile = BitConverter.ToUInt16(b, otOff+2) & 0x7FFF;
        int u = BitConverter.ToUInt16(b, otOff+4) >> 8;
        int v2 = BitConverter.ToUInt16(b, otOff+6) >> 8;
        int tOff = tilesStart + tile*65536;
        int pi = (u<256&&v2<256) ? b[tOff+v2*256+u] : 0;
        int r=pal[pi*3], g=pal[pi*3+1], bv=pal[pi*3+2];
        if (r>150 && g>120 && bv<80)
            goldPolys.Add($"TQ[{i}] OT={ti} tile={tile} uv=({u},{v2}) #{r:X2}{g:X2}{bv:X2}");
    }
    // Check TT
    for (int i = 0; i < nTT; i++) {
        int ti = BitConverter.ToUInt16(b, tqEnd+2+i*8+6) & 0x7FFF;
        int otOff = otStart+ti*20;
        int tile = BitConverter.ToUInt16(b, otOff+2) & 0x7FFF;
        int u = BitConverter.ToUInt16(b, otOff+4) >> 8;
        int v2 = BitConverter.ToUInt16(b, otOff+6) >> 8;
        int tOff = tilesStart + tile*65536;
        int pi = (u<256&&v2<256) ? b[tOff+v2*256+u] : 0;
        int r=pal[pi*3], g=pal[pi*3+1], bv=pal[pi*3+2];
        if (r>150 && g>120 && bv<80)
            goldPolys.Add($"TT[{i}] OT={ti} tile={tile} uv=({u},{v2}) #{r:X2}{g:X2}{bv:X2}");
    }

    if (goldPolys.Count > 0) {
        Console.WriteLine($"\nMesh[{m}] (nV={nV} nTQ={nTQ} nTT={nTT}):");
        foreach (var s in goldPolys) Console.WriteLine($"  {s}");
    }
}
