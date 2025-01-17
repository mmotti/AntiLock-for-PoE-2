
# AntiLock for Path of Exile 2

Providing a temporary "solution" for those of us who are struggling with constant system crashes / lockups requiring hard reboots. If you've managed to find yourself here, you know exactly what you're looking for!

As the program does mention in the console window, it takes heavy inspiration from [PoEUncrasher](https://github.com/Kapps/PoEUncrasher) by Kapps. It's also been greatly assisted by the use of Claude 3.5 Sonnet.

![Terminal window preview](/resources/AntiLock.png)

### How do I use it?
Simply launch AntiLock.exe and then launch PoE 2.

**I'd reccomend that you launch AntiLock prior to launching PoE 2**.

### Why does it require Administrator access?
It seems that, at least from what I can tell, you are not able to set process priorties to "Realtime" unless running in an elevated context and with the extent of the crashing that I've had on my system, Realtime priority has provided the best level of crash reduction.

### What does this "fix"?
At least with my specific hardware combination:
1. Crashes during game startup.
2. Crashes at character selection.
3. Crashes after interacting with portals, travelling to different waypoints etc.

### What does this not "fix"?
Crashes that are unrelated to system lockup issues (e.g. texture errors or simple crashes due to other bugs).

### How does it work?
We continually monitor PoE 2's log file and apply CPU affinity restrictions and raise the process priority to Realtime when it's determined that you are transitioning to/from a loading screen (or initially booting up the game).

We also monitor the process to make sure that it's "responding" and if not, apply the same restrictions / priorities and give it a timeout before we attempt to gracefully close it (on my PC I can't even tab out when it crahses). 

The log file processing should overrule the "responding" state of the process as if the game is still updating the log file with lines that we're actively looking for.

## Notes

#### How exactly does this differ from the PoEUncrasher?
It's largely the same in terms of detection methods and the actions that are applied - It's mostly just my own spin on things.

#### So, if there's already a solution, why make your own that functions in the same way?
I've been wanting to learn C# for a while, and this is the first practical example I've had to attempt to resolve an issue that's been affecting me directly, if anything the crashing has only gotten worse after their subequent updates (at least as of 01/25) and it's been unplayable.

#### OK, but could you not have at least changed how you determine what the game client is doing?
I tried. Initially I was monitoring for CPU usage and adjusting priorities based on that, but the CPU usage spikes fairly high during periods of loading within PoE 2 anyway so to detect abnormalities you need to monitor it being above the threshold for a sustained period of time... by which time it's far too late to provide any meaningful hope of avoiding a crash.

I also tried finding different areas of the log file to monitor but the areas identified by Kapps with PoEUncrasher seem to be the best bets; I couldn't any other options that would more reliably determine the game state than what has already been discovered.


