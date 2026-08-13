@echo off
set OUT=%1
ffmpeg -hide_banner -loglevel warning -y -re -f lavfi -i "aevalsrc=0.6*sin(2*PI*1000*t)*lt(mod(t\,1)\,0.03):s=48000:c=stereo" -c:a pcm_s16le %OUT%
