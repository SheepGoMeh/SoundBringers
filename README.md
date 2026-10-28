# SoundBringers
A Dalamud plugin that sends a second mix of the game's audio, with its own volume for each sound type, to another output device.

## About
Muting BGM in game also mutes it for your stream. SoundBringers builds a separate copy of the game's mix and plays it to a device of
your choice, normally a virtual audio cable that OBS captures. Your own speakers keep following the in-game settings, and the stream
follows the plugin's.

The second mix is rendered the same way the game renders its own: the same positioning, distance falloff, reverb, underwater muffling,
equalizers and time-stretched music. Only the volumes differ.

It provides:
* Its own volume and mute for BGM, Sound Effects, Voice, System Sounds, Ambient Sounds and Performance, plus a stream volume.
* A stream that ignores the in-game volume sliders, mutes and the background mute.
* An option to mute the stream while the game is in the background.
* Output modes: Shared, Low latency (Windows 10 or later), and Exclusive.

## Requirements
* A virtual audio cable driver. The plugin does not install one. Any of these work:
	* [VB-Cable](https://vb-audio.com/Cable/) (free). The plugin picks "CABLE Input" by default.
	* [Voicemeeter](https://vb-audio.com/Voicemeeter/). Pick one of its virtual inputs in the plugin.
	* Any other driver that shows up as a Windows playback device.
* Spatial audio turned off in the game's sound settings. The stream is the game's stereo mix, so it switches off while spatial audio is on.

## Setup
1. Install a virtual audio cable and restart if the installer asks for it.
2. In game, type `/soundbringers`, pick the cable under Output, and check that the status reads "Streaming".
3. In OBS, add an Audio Input Capture source for the cable's output side (for VB-Cable that's "CABLE Output").
4. Remove or mute your desktop audio capture in OBS, so the stream doesn't hear your local mix as well.
5. Set the levels in the plugin.

## Known Issues
* Sounds the game drops because Self, Party or Other PCs is at or near 0 are missing from the stream too.
* With surround speakers, the game renders a surround mix, and the stream folds it to stereo.
* The stream runs a few milliseconds behind your speakers. If OBS shows it out of sync with the video, use the source's Sync Offset.
