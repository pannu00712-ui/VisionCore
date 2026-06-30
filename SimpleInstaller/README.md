# VisionCore - Simple EXE Installer (Roman Urdu Guide)

Yeh sab kuch bina WiX, bina MSI ke - sirf Windows ke built-in tools se kaam karta hai.

## Maqsad
Client ko sirf **ek `.exe` file** dena hai. Woh double-click kare -> "Yes" dabaye
(admin permission) -> VisionCore khud install ho jaye aur background mein chalna
shuru ho jaye. Har dafa PC restart ho, yeh khud start ho jaye. Client ko kabhi
koi window, tray icon, ya settings screen nazar na aaye.

## Sabse Aasan Tareeqa - ONE-CLICK-BUILD.bat (recommended)

Koi command type nahi karni. Bas yeh karein:

1. Agar .NET SDK installed nahi hai, pehle yeh link se install kar lein
   (normal software jaisa installer hai - Next, Next, Install):
   `https://dotnet.microsoft.com/en-us/download/dotnet/8.0`
   ("**.NET SDK x64**" wala download chahiye, "Runtime" wala nahi.)

2. `SimpleInstaller` folder ke andar `ONE-CLICK-BUILD.bat` ko **double-click**
   karein.

3. Yeh script khud:
   - Check karega .NET SDK hai ya nahi.
   - Service ko publish karega (`service` folder khud bana dega).
   - `VisionCoreSetup.exe` bana dega.

4. Akhir mein "DONE!" message dikhega aur `VisionCoreSetup.exe` is folder mein
   maujood hoga. Yehi file client ko dena hai.

Agar koi error aaye, script khud bata dega kya masla hai aur kya karna hai.

---

## Manual Tareeqa (agar khud commands chalana chahein)

Solution root (`VisionCore.sln` jahan hai) se yeh command chalayen:

```
dotnet publish VisionCore.Service\VisionCore.Service.csproj -c Release -r win-x64 --self-contained true -o SimpleInstaller\service
```

Isse `SimpleInstaller\service\` folder mein `VisionCore.Service.exe` + saari
zaroori DLLs ban jayengi. Phir `SimpleInstaller` folder mein PowerShell khol
kar:

```
.\Build-Setup.ps1
```

> Agar `csc.exe` na mile error aaye, to system mein .NET Framework (Windows pe
> default already hota hai) check kar lein, ya mujhe bata dein - main alternate
> tareeqa (dotnet-based stub) bana dunga.

## Step 3 - Client ko dena

Bas `VisionCoreSetup.exe` file client ko bhej dein (USB, email, download link,
jo bhi tareeqa ho). Client:

1. Double-click kare.
2. Windows ka "Do you want to allow this app..." prompt aayega -> **Yes** dabayen.
3. Chand second mein install ho jayega, koi window nahi dikhegi.
4. VisionCore service background mein chal rahi hogi - Task Manager -> Services
   tab mein "VisionCoreService" naam se dikhegi, lekin client ko isse interact
   nahi karna.

## Auto-run guarantee

`Install.bat` `sc create ... start= auto` use karta hai - matlab Windows Service
Control Manager ko keh diya jata hai "yeh service har boot pe khud start ho",
chahe koi user login kare ya na kare. Yeh Windows ka apna native mechanism hai,
isliye 100% reliable hai.

Agar service crash ho jaye, `sc failure` command ke zariye yeh khud restart bhi
ho jayegi (1 minute baad), bina kisi manual intervention ke.

## Uninstall (agar zaroorat ho)

`Uninstall.bat` ko admin ke roop mein chalayen - yeh service stop/remove kar
dega aur files delete kar dega.

## Files in this folder

- `Install.bat` - asal install logic (service register + autostart).
- `Uninstall.bat` - service remove karne ke liye.
- `Build-Setup.ps1` - `Install.bat` + published service ko ek EXE mein pack
  karta hai.
- `service\` - yahan aapko Step 1 ka publish output rakhna hai (build se pehle).
