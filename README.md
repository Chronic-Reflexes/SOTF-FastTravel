# FastTravel Mod (RedLoader)

A Fast Travel system implemented in Sons of the Forest

Build instructions

- Place the required game DLLs from your game install (e.g. `_RedLoader\Game\`) into a folder and add them as references when building if needed:
  - `RedLoader.dll`
  - `Assembly-CSharp.dll`
  - `Sons.Gui.dll`
  - `UnityEngine.CoreModule.dll`
  - `UnityEngine.UI.dll`
  - `Unity.TextMeshPro.dll`
  - `Il2Cppmscorlib.dll`

- Build with Visual Studio (recommended): open `FastTravel.csproj`, set target to `.NET Framework 4.7.2`, add reference paths to the game DLLs, then build.

- Or build using MSBuild in a Developer Command Prompt:

```powershell
msbuild FastTravel.csproj /p:Configuration=Release
```

Deployment

- Copy `bin\Release\FastTravel.dll` to your mods folder for the game.
- Launch the game and approach a bed — you should see a "Fast Travel [F]" interaction.
