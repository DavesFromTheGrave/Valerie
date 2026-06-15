.\python_embeded\python.exe -s ComfyUI\main.py --windows-standalone-build --lowvram --fp16-vae --disable-smart-memory --disable-auto-launch
echo.
echo Lorelai camera mode: lowvram + fp16-vae flags added for RTX 3070 Ti 8GB + SDXL (cyberrealisticPony).
echo If you get OOM during selfies, try removing --lowvram or use the _fast_fp16_accumulation variant.
echo If you see this and ComfyUI did not start try updating your Nvidia Drivers to the latest. If you get a c10.dll error you need to install vc redist that you can find: https://aka.ms/vc14/vc_redist.x64.exe
pause
