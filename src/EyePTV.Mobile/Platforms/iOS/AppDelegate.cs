using AVFoundation;
using Foundation;

namespace EyePTV.Mobile;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIKit.UIApplication application, NSDictionary? launchOptions)
    {
        // Geluid ook als de stilteschakelaar aan staat (zoals elke videospeler).
        try { AVAudioSession.SharedInstance().SetCategory(AVAudioSessionCategory.Playback); }
        catch { /* zonder deze instelling speelt het geluid alleen niet bij de stilteschakelaar */ }
        return base.FinishedLaunching(application, launchOptions);
    }
}
