@echo off
rem %1 = input video, %2 = output png, %3.. = frame numbers to pull (up to 4)
set IN=%1
set OUT=%2
ffmpeg -hide_banner -loglevel error -y -i %IN% -vf "select='eq(n\,%3)+eq(n\,%4)+eq(n\,%5)+eq(n\,%6)',crop=1100:320:40:90,scale=700:204,tile=1x4" -frames:v 1 -fps_mode passthrough %OUT%
echo done
