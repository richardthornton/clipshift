@echo off
set A=C:\Users\richa\AppData\Local\Temp\claude\C--Users-richa-Projects-clipshift\ce076736-4f5c-4a53-810c-1ac845e03654\scratchpad\artifacts
ffmpeg -hide_banner -loglevel warning -y -f lavfi -i "testsrc2=size=1920x1080:rate=60" -t 10 -vf "drawtext=fontfile='C\:/Windows/Fonts/consola.ttf':text='FRAME %%{frame_num}':x=60:y=120:fontsize=96:fontcolor=white:box=1:boxcolor=black@0.7,drawtext=fontfile='C\:/Windows/Fonts/consola.ttf':timecode='00\:00\:00\:00':r=60:x=60:y=260:fontsize=96:fontcolor=yellow:box=1:boxcolor=black@0.7" -c:v h264_nvenc -profile:v high -preset p5 -rc constqp -qp 20 -bf 0 -g 60 -pix_fmt yuv420p -color_range tv -colorspace bt709 -color_primaries bt709 -color_trc bt709 -movflags frag_keyframe+empty_moov "%A%\clean.mp4"
ffmpeg -hide_banner -loglevel warning -y -f lavfi -i "aevalsrc=0.6*sin(2*PI*1000*t)*lt(mod(t\,1)\,0.03):s=48000:c=stereo" -t 10 -c:a pcm_s16le "%A%\clean.wav"
echo done
