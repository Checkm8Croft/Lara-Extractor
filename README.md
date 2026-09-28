## Lara Extractor

Lara Extractor is a tool for extract resources from Tomb Raider 1-5 (PC Only) and TRNG

- .phd
- .tr2
- .tr4
- .trc
- .dat

## File Extraction

These are the files that the program will extract:

- .wad2 (Level Objects, Movables and Statics)
- Textures in .tga format
- Sounds in .wav format (On TR2/3 you'll have to have the main.sfx file on the same directory)

### Why no .prj2?

The .prj2 files are used for the level geometry, but they are not in a format that can be easily extracted. The program had to be rewritten to support the .prj2 files, and it was not worth the effort for a tool that is only used for extracting resources.
In few words, the program have to rebuild the level geometry from the scratch, and it is not a simple task.
I'm working [In this repo](https://github.com/Checkm8Croft/PRJ2-Extractor) and in it's a work in progress

## Credits

- [Tomb Raider Chronicles](https://www.tombraiderchronicles.com/) for the Lara model used in the icon
- [Tomb Engine team](https://github.com/TombEngine) for the .wad2 file format and TombLib
