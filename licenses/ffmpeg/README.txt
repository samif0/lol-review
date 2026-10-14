FFmpeg in Revu
==============

Revu's clip tools use ffmpeg.exe, which is installed next to revu-desktop.exe.
ffmpeg.exe is FFmpeg (https://ffmpeg.org), a separate program written by the
FFmpeg developers and the authors of the libraries listed in LIBRARIES.txt.
Revu starts it as a child process and passes it command-line arguments. Revu's
own code is not linked with FFmpeg and remains under the MIT License.

This build of ffmpeg.exe is licensed under the GNU General Public License,
version 3 or (at your option) any later version. The license text is in
COPYING.GPLv3.txt. You may copy, modify and redistribute ffmpeg.exe under those
terms. It comes with NO WARRANTY, to the extent permitted by law; see sections
15 and 16 of the license.

Revu ships the program as built by BtbN/FFmpeg-Builds. The only change is the
Authenticode signature that Revu's release pipeline adds to the file.


Build identity
--------------

FFmpeg version    n8.1.2-50-g1a748fe2cd
                  (ffmpeg -version reports n8.1.2-50-g1a748fe2cd-20260831)
FFmpeg commit     1a748fe2cd43e3ead22fafb1b5b7d77f153898a8 (branch release/8.1)
Built by          BtbN/FFmpeg-Builds, release autobuild-2026-08-31-13-27,
                  variant win64-gpl-8.1
Build scripts     commit 8267213e26c1031621e6e1210fe3aa4867214f6a
Archive SHA-256   273abb45f3f9f76c303e35ff39f5bb6c23c163ae65f6244a32b7d4a7f6cf0616
Archive           https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-08-31-13-27/ffmpeg-n8.1.2-50-g1a748fe2cd-win64-gpl-8.1.zip
Toolchain         GCC 15.2.0 (crosstool-NG), binutils 2.46.0, mingw-w64

Configuration, as reported by ffmpeg -version:

  --prefix=/ffbuild/prefix --pkg-config-flags=--static
  --pkg-config=pkg-config --cross-prefix=x86_64-w64-mingw32- --arch=x86_64
  --target-os=mingw32 --enable-gpl --enable-version3 --disable-debug
  --disable-w32threads --enable-pthreads --enable-iconv --enable-zlib
  --enable-libxml2 --enable-libvmaf --enable-fontconfig --enable-libharfbuzz
  --enable-libfreetype --enable-libfribidi --enable-vulkan
  --enable-libshaderc --enable-libvorbis --disable-libxcb --disable-xlib
  --disable-libpulse --enable-gmp --enable-lzma --enable-liblcevc-dec
  --enable-opencl --enable-amf --enable-libaom --enable-libaribb24
  --enable-avisynth --enable-chromaprint --enable-libdav1d --enable-libdavs2
  --enable-libdvdread --enable-libdvdnav --disable-libfdk-aac
  --enable-ffnvcodec --enable-cuda-llvm --enable-frei0r --enable-libgme
  --enable-libkvazaar --enable-libaribcaption --enable-libass
  --enable-libbluray --enable-libjxl --enable-libmp3lame --enable-libopus
  --enable-libplacebo --enable-librist --enable-libssh --enable-libtheora
  --enable-libvpx --enable-libwebp --enable-libzmq --enable-lv2
  --enable-libvpl --enable-openal --enable-liboapv
  --enable-libopencore-amrnb --enable-libopencore-amrwb --enable-libopenh264
  --enable-libopenjpeg --enable-libopenmpt --enable-librav1e
  --enable-librubberband --enable-schannel --enable-sdl2 --enable-libsnappy
  --enable-libsoxr --enable-libsrt --enable-libsvtav1 --enable-libtwolame
  --enable-libuavs3d --disable-libdrm --enable-vaapi --enable-libvidstab
  --enable-libvvenc --disable-whisper --enable-libx264 --enable-libx265
  --enable-libxavs2 --enable-libxvid --enable-libzimg --enable-libzvbi
  --extra-cflags=-DLIBTWOLAME_STATIC --extra-cxxflags= --extra-libs=-lgomp
  --extra-ldflags=-pthread --extra-ldexeflags= --cc=x86_64-w64-mingw32-gcc
  --cxx=x86_64-w64-mingw32-g++ --ar=x86_64-w64-mingw32-gcc-ar
  --ranlib=x86_64-w64-mingw32-gcc-ranlib --nm=x86_64-w64-mingw32-gcc-nm
  --extra-version=20260831


Libraries
---------

ffmpeg.exe is statically linked. LIBRARIES.txt lists every library compiled
into it, with its source repository, the exact revision and its license. The
license texts are in the third-party folder.

Revu uses ffmpeg.exe to copy clip ranges without re-encoding, to re-encode a
clip with the libx264 encoder and FFmpeg's AAC encoder when copying fails, to
mix narration with FFmpeg's audio filters and AAC encoder, and to prepare
transcription audio with the libmp3lame encoder.


Corresponding Source
--------------------

You can get the complete Corresponding Source of this ffmpeg.exe, as defined in
section 1 of the GPLv3, at no charge:

1. FFmpeg at commit 1a748fe2cd43e3ead22fafb1b5b7d77f153898a8:
   https://github.com/FFmpeg/FFmpeg/tree/1a748fe2cd43e3ead22fafb1b5b7d77f153898a8
   (the same commit is in https://git.ffmpeg.org/ffmpeg.git)

2. The scripts that built it, at release autobuild-2026-08-31-13-27:
   https://github.com/BtbN/FFmpeg-Builds/tree/autobuild-2026-08-31-13-27
   build.sh and util/ hold the configure and build steps, variants/ and
   addins/ the win64-gpl-8.1 settings, images/ the toolchain, and scripts.d/
   the repository, revision and build steps of every library.

3. Each library's source, at the repository and revision listed in
   LIBRARIES.txt.

Written offer: for at least three years after Revu last distributes this build
of ffmpeg.exe, Sami Fawcett, Revu's maintainer, will give anyone who has a copy
of it the complete Corresponding Source in machine-readable form, either as a
download at no charge or on a durable physical medium for no more than the cost
of making and sending it. Ask by email at samifawcett.nyc@gmail.com with
"Revu FFmpeg source" in the subject, or open an issue at
https://github.com/samif0/lol-review/issues.


Acknowledgements
----------------

Some of the licenses ask for these notices:

- Portions of this software are copyright (C) 2026 The FreeType Project
  (https://freetype.org). All rights reserved.
- This software uses LAME (https://lame.sourceforge.io/).
