namespace WaBiBaBuSy.Models;

/// <summary>
/// Defines the operational mode of the WaBiBaBuSy application.
/// </summary>
public enum OperationMode
{
    /// <summary>
    /// Application is running as a client only.
    /// </summary>
    Client,

    /// <summary>
    /// Application is running as both server and client.
    /// </summary>
    Server
}
