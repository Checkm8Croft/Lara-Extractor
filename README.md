## Lara Extractor

Lara Extractor is a tool for extract resources from Tomb Raider 1-5 (PC Only), TRNG and TEN
- .phd
- .tr2
- .tr4
- .trc
- .ten
- .obj (TR4/5 PSX)
- .psx (TR1-3 PSX)

## File Extraction

These are the files that the program will extract:

- .wad2 (Level Objects, Movables and Statics)
- Textures in .tga format
- Sounds in .wav format (On TR2/3 you'll have to have the main.sfx file on the same directory)

## PlayStation (PSX) support

TR4 and TR5 (Chronicles) PlayStation levels are read from `GAMEWAD.OBJ`, the file that contains all the levels of the disc.
For every level in it, the program extracts:

- Textures of the level rooms in .tga format (one folder per level)
- Sounds in .wav format (11025 Hz, mono)
- .wad2 files, one per level (Moveables with bones and animations, Statics and Sprites)

### Known limitations of the PSX .wad2

- **Lara, her weapons and the other resident meshes are missing.** In the level files their mesh pointers are empty and I could not find the meshes in any file of the disc. The Moveables that use them are skipped (Lara) or have empty meshes on the missing bones (weapons and similar objects); animations are there
- Texture animations, polygon blend flags and the sound pitch are not converted
- The names of the levels are not known, so files are named by their position inside `GAMEWAD.OBJ` (for example `Level_03`)

### Why no .prj2?

The .prj2 files are used for the level geometry, but they are not in a format that can be easily extracted. The program had to be rewritten to support the .prj2 files, and it was not worth the effort for a tool that is only used for extracting resources.
In few words, the program have to rebuild the level geometry from the scratch, and it is not a simple task.
I'm working [In this repo](https://github.com/Checkm8Croft/PRJ2-Extractor) and in it's a work in progress

## Credits

- [Tomb Raider Chronicles](https://www.tombraiderchronicles.com/) for the Lara model used in the icon
- [Tomb Engine team](https://github.com/TombEngine) for TombLib and TombEngine code used for the program
- [TOMB5](https://github.com/TOMB5/TOMB5) helped me with TR4/5 PSX extraction
- [trview](https://github.com/chreden/trview) helped me with TR1-3 PSX extraction