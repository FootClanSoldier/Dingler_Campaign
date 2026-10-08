# Dingler

> **`Campaign` branch:** Work in progress to restore the original HEX PvE campaign in [Blitzkind/Dingler](https://github.com/Blitzkind/Dingler).

## Information
A server emulator for Hex: Shards of Fate written in C# that attempts to mimic how it ran prior to its shutdown.

## Requirements
- Visual Studio 2026 or Rider
- Most recent Hex client from either Steam or standalone

## Installation
- Download the latest release and extract somewhere on your pc.
- Copy and paste the following dlls from your Hex client installation into the extracted Dingler.Terminal folder
  - Assembly-CSharp-firstpass.dll
  - ICSharpCode.SharpZipLib.dll
  - NCalc.dll
  - SampleClassLibrary.dll
  - System.EnterpriseServices.dll
  - System.Web.Services.dll
  - UnityEngine.dll
- Update the appsettings.json file's "GamedataLocation" entry to point at your Hex: Shards of Fate installation directory.
  - If installed fresh from Steam this is {steamFolder}/steamapps/common/HEX SHARDS OF FATE
- Update your client to point at the game server and auth. To just run locally by default these should be changed in config.ini in the above Hex directory
  - GameServerIP=127.0.0.1
  - CZEAuthUrl=http://localhost:5000/auth/hexlogin
- If at any point someone hosts a real server for gameplay, you'll point at their urls instead.
- Boot Dingler.Auth first followed by Dingler.Terminal
- Hit Start Server and wait for it to boot
- Enjoy playing Hex again!

During non local deployment, you can change the address the server binds to by editing the following value within appsettings.json within Dingler.Terminal
  - Dingler
    - Endpoints
      - TCP
        - Url 

## Known issues
While this attempts to be as accurate as possible there are some issues that present themselves as backend bugs when they are just presentational.
- Sir Pies, Brown Fox Scout, and Subterranean Spy do not work correctly. The current implementation of the rules engine does not reflect when the game has extra known zones and therefore does not send the card info correctly to the players
- The Tournament system is held together with hopes and dreams. It may have leaks. It may break. It's so rough man.
- If you see any other issues please report it. Let it be known that any bugs that existed in the final version of Hex will likely be left alone. If at some point we want to make this the definitive version we can do that

## Supported features
- Everyone gets four of every card. No packs. Just straight everything.
- 1v1 matches for standard and immortal

## Roadmap
- Direct challenges
- 8+ man tournaments
- Ability to schedule tournaments for you and your friends
- Limited
- Special formats like Rock and Corinth
- Making the tournament system not so embarassing

## FAQ
- How do I use this?
  - The recommended way to use Dingler with mulple users without a full hosting solution is the use Hamachi. Instructions [here](https://github.com/Blitzkind/Dingler/wiki/Hamachi-Instructions)
- How do I register? There's no page.
  - I didn't want to bother making a bespoke registration page. Just type your username in with any password. If that user does not exist, congrats, you own it with that password.
- Will PVE be supported?
  - At this point, I don't have the bandwidth, that being said, if you want to work on it, fork it and let's gooooooo.
- Why are the releases so large?
  - In order to keep using these easy for all users, the compiled binaries are self contained so you don't need to download or install a huge number of dependencies from Microsoft Note: this does not mean any proprietary data owned by IP holders is included. This is just the .Net dependencies
- Where are decks and other information saved?
  - Upon first boot a data folder will be created in the root folder of Auth and Terminal. Those folders will contain sqlite files that hold that information
- I tried to run my client and it gave me a werid error with no login prompt.
  - You're likely trying to run this from the steam app proper. While there is possibly some way to make this work, Dingler currently doesn't support this. You'll need to go into your install directory and run Hex.exe manually
