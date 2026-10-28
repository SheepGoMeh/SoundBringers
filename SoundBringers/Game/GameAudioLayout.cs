using System;

namespace SoundBringers.Game;

// game structure offsets and sizes
internal static partial class GameAudio
{
	// CoreSourceVoice
	public const int VoiceState = 0xA8; // cs-audio: CoreSourceVoice.State
	public const int VoiceCallback = 0xB0; // cs-audio: CoreSourceVoice.Callback
	public const int VoiceSampleType = 0xB8; // cs-audio: CoreSourceVoice.SampleType
	public const int VoiceSampleRate = 0xBC; // cs-audio: CoreSourceVoice.SampleRate
	public const int VoiceInputChannels = 0xD4; // cs-audio: CoreSourceVoice.InputChannels
	public const int VoiceQueuedBufferCount = 0xD8; // cs-audio: CoreSourceVoice.QueuedBufferCount
	public const int VoiceCurrentBufferIndex = 0xDC; // cs-audio: CoreSourceVoice.CurrentBufferIndex
	public const int VoiceBufferReadOffset = 0xE0; // cs-audio: CoreSourceVoice.BufferReadOffset
	public const int VoiceBuffers = 0xE8; // cs-audio: CoreSourceVoice.Buffers
	public const int VoicePreviousVolume = 0x938; // cs-audio: CoreSourceVoice.PreviousVolume
	public const int VoiceEffects = 0x998; // cs-audio: CoreSourceVoice.Effects
	public const int VoiceEffectCount = 0x9B8; // cs-audio: CoreSourceVoice.EffectCount
	public const int VoiceCurrentParams = 0x530; // cs-audio: CoreSourceVoice.CurrentParams
	public const int VoiceSends = 0x940; // cs-audio: CoreSourceVoice.Sends
	public const int VoiceSendCount = 0x990; // cs-audio: CoreSourceVoice.SendCount
	public const int VoiceStateRendered = 3; // cs-audio: CoreSourceVoice.State (rendered while 3)

	// CoreSourceVoice.QueuedBuffer
	public const int VoiceBufferCount = 2; // cs-audio: CoreSourceVoice.Buffers (FixedSizeArray2)
	public const int QueuedBufferSize = 0x20; // cs-audio: CoreSourceVoice.QueuedBuffer (size)
	public const int QueuedBufferData = 0x00; // cs-audio: CoreSourceVoice.QueuedBuffer.Data
	public const int QueuedBufferBytes = 0x08; // cs-audio: CoreSourceVoice.QueuedBuffer.Size

	// VoiceParams (inside CurrentParams/PendingParams)
	public const int ParamsVolume = 0x00; // cs-audio: CoreSourceVoice.VoiceParams.Volume
	public const int ParamsPitch = 0x04; // cs-audio: CoreSourceVoice.VoiceParams.Pitch

	// VoiceSend
	public const int SendSize = 0x10; // cs-audio: VoiceSend (size)
	public const int SendDestination = 0x00; // cs-audio: VoiceSend.Destination
	public const int SendGain = 0x08; // cs-audio: VoiceSend.Gain

	// CoreSubmixVoice
	public const int SubmixSends = 0x90; // cs-audio: CoreSubmixVoice.Sends
	public const int SubmixSendCount = 0xE0; // cs-audio: CoreSubmixVoice.SendCount
	public const int SubmixEffects = 0xE8; // cs-audio: CoreSubmixVoice.Effects
	public const int SubmixEffectCount = 0x108; // cs-audio: CoreSubmixVoice.EffectCount

	// CoreMasterVoice
	public const int MasterEffects = 0x90; // cs-audio: CoreMasterVoice.Effects
	public const int MasterEffectCount = 0xB0; // cs-audio: CoreMasterVoice.EffectCount
	public const int MasterBusId = 0xDC; // cs-audio: CoreMasterVoice.BusId

	// Bus (four faders; value = Current * Scale)
	public const int BusVolume = 0x18; // cs-audio: Bus.Volume
	public const int BusVolume2 = 0x40; // cs-audio: Bus.Volume2
	public const int BusVolume3 = 0x68; // cs-audio: Bus.Volume3
	public const int BusVolume4 = 0x90; // cs-audio: Bus.Volume4

	// RootSound
	public const int RootBusId = 0x19; // cs-audio: RootSound.BusId
	public const int RootExtendedFlags = 0x30; // cs-audio: RootSound.ExtendedFlags
	public const int RootPlayerCategory = 0x450; // cs-audio: RootSound.PlayerCategory

	// voice -> RootSound chain (AudioProbe): callback - 8 = VoiceSead, +0x10 sound wrapper, +0x10 RootSound
	public const int VoiceSeadBack = 8;
	public const int WrapperOffset = 0x10;
	public const int RootOffset = 0x10;

	// SoundManager per-category float arrays (GetEffectiveVolume, 0x140410A80): the slider and four game-driven ones, index = bus id
	public const int ManagerFactor2 = 0x90;
	public const int ManagerFactor3 = 0xE4;
	public const int ManagerFactor4 = 0x138;
	public const int ManagerFactor5 = 0x18C;
}
