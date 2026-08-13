$dir = "C:\Users\richa\AppData\Local\Temp\claude\C--Users-richa-Projects-clipshift\ce076736-4f5c-4a53-810c-1ac845e03654\scratchpad"
$out = Join-Path $dir "artifacts"
New-Item -ItemType Directory -Force $out | Out-Null

# Three processes started together, killed together: a plain fragmented MP4,
# a fragmented MP4 whose first sample is presented 0.25s (15 frames) late so an
# edit list is written, and a WAV with a 1kHz click on every second boundary.
$vp = Start-Process cmd -ArgumentList "/c", "$dir\enc-video.cmd", "$out\killed.mp4" -PassThru -NoNewWindow
$op = Start-Process cmd -ArgumentList "/c", "$dir\enc-video-offset.cmd", "$out\killed-offset.mp4" -PassThru -NoNewWindow
$ap = Start-Process cmd -ArgumentList "/c", "$dir\enc-audio.cmd", "$out\killed.wav" -PassThru -NoNewWindow

Start-Sleep -Milliseconds 30400

taskkill /PID $vp.Id /T /F | Out-Null
taskkill /PID $op.Id /T /F | Out-Null
taskkill /PID $ap.Id /T /F | Out-Null
Start-Sleep -Seconds 1

Get-ChildItem $out | Select-Object Name, Length, LastWriteTime
