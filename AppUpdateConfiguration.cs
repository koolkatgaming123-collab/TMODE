using System;
namespace TourneyMode;
public static class AppUpdateConfiguration
{
    public const string PublicKeyPem = """
-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEArylKTb7u35Dk7K0RsUip
lDLG+4YryaaAszyn/jezkqxx+Tv7qwwCsWXFD8a86WUm3+Qfbxz4Pn9SilWGRe3l
+LUcwhrO6FQaWSAdcNVBFHQrL84M9aHjBHbT9CJ2a4oDpvDUmyMeKxe2fN1yNPPc
UiNupo8yd+isS8mzeqNwb2CESI1ysXLYaZW3sVv5272JHUfyftADoc2esTT8o3g8
VbWMMT7ibxBGdLhPf1YFqi8u+b31bykjJ9AtohUdrIjtxjuuY5PhSPMsVSKZ0tLt
Q8V6wJP681YAWrG6yZu1+8SsLPWrLjVvaw0OYq4ctdwL3g1Zzyb6GyuVU53sIp55
33KKjkauY8OmFf1rkCByCFScDp+N9fHe+q9EvGx5vkVQ50uj8wOLjTV3+Vd6AHgc
Fr+hfp0biBXgxQiG0FXKIF3Y+DAPlxsiZfwNfQvuDdWasfn1X2MlgnZUTXOjidzt
Z7FHIq7qyCeWMu2AiWNkfUT/YmCUbcBziSI2MuyPLC95AgMBAAE=
-----END PUBLIC KEY-----
""";
    public static AppUpdater CreateUpdater(TourneyEngine? engine = null) => new(new UpdateSettings
    {
        PublicKeyPem = PublicKeyPem,
        IsEngineActive = engine is null ? null : () => engine.IsActive,
        IsGameRunning = engine is null ? null : () => engine.GameClientRunning
    });
}
