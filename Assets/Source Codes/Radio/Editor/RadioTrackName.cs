using System;
using System.Text.RegularExpressions;

namespace RageRoom.EditorTools
{
    /// <summary>Lit l'artiste et le titre dans un nom de fichier audio, pour pré-remplir une radio.</summary>
    static class RadioTrackName
    {
        // Numéro de piste en tête de nom : « 01 », « 01. », « 01 - », « 1) »… suivi d'un espace
        static readonly Regex TrackNumber = new Regex(@"^\s*\d{1,3}\s*[-._)]*\s+");

        /// <summary>
        /// « 03 - Artiste - Titre » donne Artiste / Titre. Sans « - » dans le nom, tout va dans le titre
        /// et l'artiste reste vide. Les tirets bas sont lus comme des espaces.
        /// </summary>
        public static void Parse(string fileName, out string artist, out string title)
        {
            string name = (fileName ?? "").Replace('_', ' ').Trim();
            string stripped = TrackNumber.Replace(name, "").Trim();
            if (stripped.Length > 0) name = stripped;

            int separator = name.IndexOf(" - ", StringComparison.Ordinal);
            if (separator > 0 && separator + 3 < name.Length)
            {
                artist = name.Substring(0, separator).Trim();
                title = name.Substring(separator + 3).Trim();
            }
            else
            {
                artist = "";
                title = name;
            }
        }
    }
}
