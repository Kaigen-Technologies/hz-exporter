using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine.UIElements;

namespace HZExporter
{
    /// <summary>Persistent Scene-view button to play/stop the hz-engine.</summary>
    [Overlay(typeof(UnityEditor.SceneView), "HZ Engine", true)]
    public class HZEngineOverlay : Overlay
    {
        Button _button;

        public override VisualElement CreatePanelContent()
        {
            var root = new VisualElement();
            _button = new Button(OnClick);
            root.Add(_button);
            Refresh();
            return root;
        }

        public override void OnCreated() => EditorApplication.update += Refresh;
        public override void OnWillBeDestroyed() => EditorApplication.update -= Refresh;

        void OnClick()
        {
            if (HZEnginePlayer.IsRunning) HZEnginePlayer.Stop();
            else HZEnginePlayer.Play();
        }

        void Refresh()
        {
            if (_button == null) return;
            _button.text = HZEnginePlayer.IsRunning ? "■ Stop Engine" : "▶ Play Engine";
        }
    }
}
