using SocAlytics.Analysts.Manager.Diagnostics;

namespace SocAlytics.Analysts.Manager.DeviceKeys;

public sealed class DeviceKeyException : Exception
{
    public DeviceKeyException(string code)
        : base(code)
    {
        Code = ManagerDiagnosticCodes.IsKnown(code) ? code : throw new ArgumentOutOfRangeException(nameof(code));
    }

    public string Code { get; }
}
