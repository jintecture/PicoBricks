using UnityEngine;

// Spoken guidance for the visitor: plays one short recorded line per moment of the experience.
// The recordings are Assets/Resources/Voice/<key>.mp3 (made from the script in docs/voice_script.md with
// tools/make_voice.py). A missing recording is simply skipped, so the app works without them.
// Keys: intro_welcome (IntroSequence), welcome, draw, too_short, preview, no_fit, ghost_start, ghost_done, error.
// Adds itself to the scene at startup (no scene edit needed).
public class VoiceGuide : MonoBehaviour
{
    static VoiceGuide instance;
    AudioSource source;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Install()
    {
        if (instance == null) new GameObject("VoiceGuide").AddComponent<VoiceGuide>();
    }

    void Awake()
    {
        instance = this;
        source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0f; // plain voice in both ears, wherever the visitor looks
    }

    /// <summary>Play the line for this moment (stops the previous one). Does nothing if there is no recording.</summary>
    public static void Play(string key)
    {
        if (instance == null || string.IsNullOrEmpty(key)) return;
        var clip = Resources.Load<AudioClip>("Voice/" + key);
        if (clip == null) return;
        instance.source.Stop();
        instance.source.clip = clip;
        instance.source.Play();
    }
}
