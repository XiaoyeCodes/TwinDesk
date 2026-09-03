using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace Workbench.Windows;

public sealed record EncoderInfo(string Name, Guid Clsid, bool Hardware, bool ActivationSucceeded, string? ActivationError);

/// <summary>Enumerates and activates candidates. This does not prove that frame encoding works.</summary>
public static class EncoderCatalog
{
    public static IReadOnlyList<EncoderInfo> ProbeH264()
    {
        MediaFactory.MFStartup().CheckError();
        try
        {
            var results = new List<EncoderInfo>();
            Enumerate(0x04, true, results); // MFT_ENUM_FLAG_HARDWARE
            Enumerate(0x03, false, results); // SYNCMFT | ASYNCMFT
            return results;
        }
        finally { MediaFactory.MFShutdown().CheckError(); }
    }

    private static void Enumerate(uint categoryFlags, bool hardware, List<EncoderInfo> results)
    {
        var output = new RegisterTypeInfo
        {
            GuidMajorType = new Guid("73646976-0000-0010-8000-00aa00389b71"), // MFMediaType_Video
            GuidSubtype = new Guid("34363248-0000-0010-8000-00aa00389b71") // MFVideoFormat_H264
        };
        nint pointers = 0;
        uint count = 0;
        try
        {
            Marshal.ThrowExceptionForHR(MFTEnumEx(TransformCategoryGuids.VideoEncoder,
                categoryFlags | 0x40, 0, in output, out pointers, out count)); // SORTANDFILTER
            for (uint i = 0; i < count; i++)
            {
                var pointer = Marshal.ReadIntPtr(pointers, checked((int)i * IntPtr.Size));
                // Retain the enumeration's reference for the finally block; wrapper owns this extra reference.
                Marshal.AddRef(pointer);
                using var activation = new IMFActivate(pointer);
                string name = activation.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
                Guid clsid = activation.GetGUID(TransformAttributeKeys.MftTransformClsidAttribute);
                bool activated = false;
                string? error = null;
                try
                {
                    using var transform = activation.ActivateObject<IMFTransform>();
                    activated = true;
                }
                catch (Exception exception)
                {
                    error = $"{exception.GetType().Name}: 0x{exception.HResult:X8} {exception.Message}";
                }
                finally
                {
                    if (activated) activation.ShutdownObject();
                }
                results.Add(new(name, clsid, hardware, activated, error));
            }
        }
        finally
        {
            if (pointers != 0)
            {
                for (uint i = 0; i < count; i++)
                {
                    var pointer = Marshal.ReadIntPtr(pointers, checked((int)i * IntPtr.Size));
                    if (pointer != 0) Marshal.Release(pointer);
                }
                Marshal.FreeCoTaskMem(pointers);
            }
        }
    }

    // Caller owns the selected activation and must keep MFStartup active until it is shut down.
    internal static IMFActivate OpenH264Activation(bool hardware, int candidateIndex)
    {
        if (candidateIndex < 0) throw new ArgumentOutOfRangeException(nameof(candidateIndex));
        var output = new RegisterTypeInfo
        {
            GuidMajorType = new("73646976-0000-0010-8000-00aa00389b71"),
            GuidSubtype = new("34363248-0000-0010-8000-00aa00389b71")
        };
        nint pointers = 0;
        uint count = 0;
        try
        {
            Marshal.ThrowExceptionForHR(MFTEnumEx(TransformCategoryGuids.VideoEncoder,
                (hardware ? 0x04u : 0x01u) | 0x40, 0, in output, out pointers, out count));
            if (candidateIndex >= count) throw new InvalidOperationException($"H264 candidate {candidateIndex} unavailable; count={count}, hardware={hardware}.");
            var pointer = Marshal.ReadIntPtr(pointers, checked(candidateIndex * IntPtr.Size));
            Marshal.AddRef(pointer);
            return new IMFActivate(pointer);
        }
        finally
        {
            if (pointers != 0)
            {
                for (uint i = 0; i < count; i++) Marshal.Release(Marshal.ReadIntPtr(pointers, checked((int)i * IntPtr.Size)));
                Marshal.FreeCoTaskMem(pointers);
            }
        }
    }

    // Explicit ownership avoids relying on a collection wrapper to free the original COM pointer array.
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFTEnumEx(Guid category, uint flags, nint inputType,
        in RegisterTypeInfo outputType, out nint activations, out uint count);
}
