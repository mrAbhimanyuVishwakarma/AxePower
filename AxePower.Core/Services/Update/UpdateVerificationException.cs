using System;

namespace AxePower.Core.Services.Update;

/// <summary>
/// Thrown when an update asset or manifest fails security, cryptographic, or Authenticode verification.
/// </summary>
public class UpdateVerificationException : Exception
{
    public UpdateVerificationException(string message) : base(message)
    {
    }

    public UpdateVerificationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
