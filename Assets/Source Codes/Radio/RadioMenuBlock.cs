using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace RageRoom
{
    /// <summary>
    /// Branche le bloc radio des menus (Radio.uxml) sur le RadioPlayer : choix de la radio, morceau en cours
    /// (pochette, titre, artiste, album), morceau précédent / suivant et volume de la musique.
    /// À recréer chaque fois que le menu est (ré)activé : un UI Document reconstruit ses éléments à chaque activation.
    /// Le bloc se met à jour tout seul quand le morceau change, et se débranche quand le menu se ferme.
    /// </summary>
    public sealed class RadioMenuBlock
    {
        const string OffClass = "radio--off";

        readonly VisualElement block, cover;
        readonly Label stationName, title, artist, album, volumeValue;
        readonly Button stationPrev, stationNext, trackPrev, trackNext;
        readonly Slider volume;

        public RadioMenuBlock(VisualElement root)
        {
            block = Find<VisualElement>(root, "radio");
            stationPrev = Find<Button>(root, "radio-station-prev");
            stationName = Find<Label>(root, "radio-station-name");
            stationNext = Find<Button>(root, "radio-station-next");
            cover = Find<VisualElement>(root, "radio-cover");
            title = Find<Label>(root, "radio-title");
            artist = Find<Label>(root, "radio-artist");
            album = Find<Label>(root, "radio-album");
            trackPrev = Find<Button>(root, "radio-track-prev");
            trackNext = Find<Button>(root, "radio-track-next");
            volume = Find<Slider>(root, "radio-volume-slider");
            volumeValue = Find<Label>(root, "radio-volume-value");

            OnClick(stationPrev, () => { if (RadioPlayer.Instance != null) RadioPlayer.Instance.PreviousStation(); });
            OnClick(stationNext, () => { if (RadioPlayer.Instance != null) RadioPlayer.Instance.NextStation(); });
            OnClick(trackPrev, () => { if (RadioPlayer.Instance != null) RadioPlayer.Instance.PreviousTrack(); });
            OnClick(trackNext, () => { if (RadioPlayer.Instance != null) RadioPlayer.Instance.NextTrack(); });

            if (volume != null)
            {
                volume.lowValue = 0f;
                volume.highValue = 1f;
                volume.RegisterValueChangedCallback(evt =>
                {
                    if (RadioPlayer.Instance != null) RadioPlayer.Instance.Volume = evt.newValue;
                    RefreshVolume();
                });
            }

            // Le menu suit la radio tant qu'il est affiché, et se débranche quand ses éléments sont retirés.
            if (block != null)
            {
                RadioPlayer.Changed += Refresh;
                block.RegisterCallback<DetachFromPanelEvent>(evt => RadioPlayer.Changed -= Refresh);
            }

            Refresh();
        }

        /// <summary>Recopie l'état de la radio dans le bloc.</summary>
        public void Refresh()
        {
            RadioPlayer player = RadioPlayer.Instance;
            RadioStation station = player != null ? player.Station : null;
            RadioTrack track = player != null ? player.Track : null;
            bool hasStations = player != null && player.Stations.Count > 0;
            bool uppercase = player == null || player.Config == null || player.Config.uppercase;

            if (stationName != null)
                stationName.text = station != null ? Text(station.stationName, uppercase)
                                 : hasStations ? "RADIO COUPÉE" : "AUCUNE RADIO";

            // Radio coupée : le morceau et ses boutons passent en grisé
            if (block != null) block.EnableInClassList(OffClass, track == null);
            if (stationPrev != null) stationPrev.SetEnabled(hasStations);
            if (stationNext != null) stationNext.SetEnabled(hasStations);
            if (trackPrev != null) trackPrev.SetEnabled(track != null);
            if (trackNext != null) trackNext.SetEnabled(track != null);

            if (title != null) title.text = track != null ? Text(track.title, uppercase) : "AUCUN MORCEAU";
            if (artist != null) artist.text = track != null ? Text(track.artist, uppercase) : "";
            if (album != null) album.text = track != null ? Text(track.album, uppercase) : "";

            if (cover != null)
            {
                Texture2D texture = station != null && track != null ? station.CoverOf(track) : null;
                cover.style.backgroundImage = texture != null ? new StyleBackground(texture) : new StyleBackground(StyleKeyword.None);
            }

            if (volume != null && player != null) volume.SetValueWithoutNotify(player.Volume);
            RefreshVolume();
        }

        void RefreshVolume()
        {
            if (volumeValue != null && volume != null) volumeValue.text = Mathf.RoundToInt(volume.value * 100f) + " %";
        }

        static string Text(string value, bool uppercase)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return uppercase ? value.ToUpperInvariant() : value;
        }

        static T Find<T>(VisualElement root, string elementName) where T : VisualElement
        {
            T element = root != null ? root.Q<T>(elementName) : null;
            if (element == null)
                Debug.LogError($"[Radio] Élément introuvable dans le UXML du menu : il faut un {typeof(T).Name} nommé \"{elementName}\" "
                             + "(voir Radio.uxml). Vérifie son type et son champ Name dans UI Builder.");
            return element;
        }

        static void OnClick(Button button, Action action)
        {
            if (button != null) button.clicked += action;
        }
    }
}
