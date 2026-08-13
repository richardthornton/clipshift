@echo off
set OUT=%1
set EXTRA=%~2
ffmpeg -hide_banner -loglevel warning -y -re -f lavfi -i "testsrc2=size=1920x1080:rate=60" -vf "drawtext=fontfile='C\:/Windows/Fonts/consola.ttf':text='FRAME %%{frame_num}':x=60:y=120:fontsize=96:fontcolor=white:box=1:boxcolor=black@0.7,drawtext=fontfile='C\:/Windows/Fonts/consola.ttf':timecode='00\:00\:00\:00':r=60:x=60:y=260:fontsize=96:fontcolor=yellow:box=1:boxcolor=black@0.7" -c:v h264_nvenc -profile:v high -preset p5 -rc constqp -qp 20 -bf 0 -g 60 -pix_fmt yuv420p -color_range tv -colorspace bt709 -color_primaries bt709 -color_trc bt709 %EXTRA% -movflags frag_keyframe+empty_moov %OUT%
