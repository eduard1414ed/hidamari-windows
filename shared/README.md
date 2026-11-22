# shared/

Files the Windows app shares with the Mac app. The master copies live in the Mac app's repository, which copies them here with one command; do not edit them here, a change here is overwritten by the next copy.

| | |
|---|---|
| `wall.html` | the wall's picture: the same file the Mac app and the website draw with |
| `Strings.swift` | the Mac app's texts; `tools/check-strings.py` compares the Windows app's texts with it |
| `thumbs/` | the scene and plant pictures of the card |
| `grain.png` | the card's grain |
| `fonts/` | IBM Plex Mono with its licence (`OFL.txt`) |
| `textures/` | the pictures the Direct3D drawing reads (crowns, plants, frost): the Mac app's Metal textures, the same bytes, repacked from LZFSE into gzip |
