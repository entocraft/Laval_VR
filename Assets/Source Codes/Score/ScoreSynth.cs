using System;

namespace RageRoom
{
    /// <summary>
    /// Sons du HUD fabriqués par calcul, sans fichier audio : chaque fonction renvoie les échantillons d'un son
    /// court (mono, 44,1 kHz, valeurs entre -1 et 1). ScoreHud en fait des AudioClip au lancement.
    /// Pour utiliser tes propres sons à la place, glisse des clips dans la section « HUD : sons » du barème.
    /// </summary>
    public static class ScoreSynth
    {
        public const int SampleRate = 44100;

        const double Tau = Math.PI * 2.0;

        /// <summary>Choc sourd : une ligne qui s'écrase sur le HUD.</summary>
        public static float[] Thud()
        {
            float[] s = New(0.18f);
            var noise = new Random(11);
            double phase = 0, low = 0;
            for (int i = 0; i < s.Length; i++)
            {
                double t = i / (double)SampleRate;
                // Grave qui descend très vite : 240 Hz -> 90 Hz (assez haut pour les petits haut-parleurs du casque).
                double f = 90 + 150 * Math.Exp(-t / 0.03);
                phase += Tau * f / SampleRate;
                double body = Math.Sin(phase) * Math.Exp(-t / 0.05);
                // Claquement : bruit filtré, très bref.
                low += 0.25 * ((noise.NextDouble() * 2 - 1) - low);
                double click = low * Math.Exp(-t / 0.008) * 1.6;
                s[i] = (float)(body + click);
            }
            return Finish(s, 0.9f);
        }

        /// <summary>Tintement de combo : une note brève, que le HUD joue de plus en plus aiguë à chaque casse.</summary>
        public static float[] Tick()
        {
            float[] s = New(0.14f);
            const double f = 660;
            for (int i = 0; i < s.Length; i++)
            {
                double t = i / (double)SampleRate;
                double env = Math.Min(1, t / 0.002) * Math.Exp(-t / 0.035);
                // Fondamentale + deux harmoniques : timbre de cloche d'arcade.
                double v = Math.Sin(Tau * f * t) + 0.45 * Math.Sin(Tau * f * 2 * t) + 0.2 * Math.Sin(Tau * f * 3.01 * t);
                s[i] = (float)(v * env);
            }
            return Finish(s, 0.8f);
        }

        /// <summary>Bonus : gros impact suivi d'un accord brillant.</summary>
        public static float[] Slam()
        {
            float[] s = New(0.6f);
            var noise = new Random(23);
            double phase = 0, low = 0;
            double[] chord = { 523.25, 659.25, 783.99, 1046.5 }; // do, mi, sol, do
            for (int i = 0; i < s.Length; i++)
            {
                double t = i / (double)SampleRate;

                double f = 70 + 170 * Math.Exp(-t / 0.04);
                phase += Tau * f / SampleRate;
                double body = Math.Sin(phase) * Math.Exp(-t / 0.09) * 1.3;

                low += 0.5 * ((noise.NextDouble() * 2 - 1) - low);
                double crash = low * Math.Exp(-t / 0.045) * 0.6;

                // Accord en dents de scie adoucies, qui démarre juste après l'impact.
                double tc = t - 0.012, ring = 0;
                if (tc > 0)
                {
                    double env = Math.Min(1, tc / 0.004) * Math.Exp(-tc / 0.16);
                    for (int n = 0; n < chord.Length; n++)
                        for (int h = 1; h <= 4; h++)
                            ring += Math.Sin(Tau * chord[n] * h * tc) / (h * h) * 0.28;
                    ring *= env;
                }
                s[i] = (float)(body + crash + ring);
            }
            return Finish(s, 0.95f);
        }

        /// <summary>Multiplicateur qui monte : trois notes rapides vers l'aigu.</summary>
        public static float[] Rise()
        {
            float[] s = New(0.34f);
            double[] notes = { 659.25, 880.0, 1318.5 };
            const double step = 0.06;
            for (int i = 0; i < s.Length; i++)
            {
                double t = i / (double)SampleRate, v = 0;
                for (int n = 0; n < notes.Length; n++)
                {
                    double tn = t - n * step;
                    if (tn < 0) continue;
                    double decay = n == notes.Length - 1 ? 0.09 : 0.03;
                    double env = Math.Min(1, tn / 0.002) * Math.Exp(-tn / decay);
                    v += (Math.Sin(Tau * notes[n] * tn) + 0.35 * Math.Sin(Tau * notes[n] * 3 * tn)) * env;
                }
                s[i] = (float)v;
            }
            return Finish(s, 0.8f);
        }

        /// <summary>Fin de combo : deux notes claires, comme des points encaissés.</summary>
        public static float[] Bank()
        {
            float[] s = New(0.5f);
            double[] notes = { 987.77, 1318.5 };
            for (int i = 0; i < s.Length; i++)
            {
                double t = i / (double)SampleRate, v = 0;
                for (int n = 0; n < notes.Length; n++)
                {
                    double tn = t - n * 0.08;
                    if (tn < 0) continue;
                    double decay = n == 0 ? 0.05 : 0.14;
                    double env = Math.Min(1, tn / 0.002) * Math.Exp(-tn / decay);
                    v += (Math.Sin(Tau * notes[n] * tn) + 0.3 * Math.Sin(Tau * notes[n] * 2 * tn)) * env;
                }
                s[i] = (float)v;
            }
            return Finish(s, 0.75f);
        }

        static float[] New(float seconds)
        {
            return new float[(int)(seconds * SampleRate)];
        }

        /// <summary>Met le son au niveau voulu et adoucit sa fin pour éviter un clic.</summary>
        static float[] Finish(float[] s, float peak)
        {
            float max = 1e-6f;
            for (int i = 0; i < s.Length; i++) max = Math.Max(max, Math.Abs(s[i]));
            float gain = peak / max;

            int fade = Math.Min(s.Length, SampleRate / 200); // 5 ms
            for (int i = 0; i < s.Length; i++)
            {
                float v = s[i] * gain;
                int fromEnd = s.Length - 1 - i;
                if (fromEnd < fade) v *= fromEnd / (float)fade;
                s[i] = v;
            }
            return s;
        }
    }
}
