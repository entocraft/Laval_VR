using System;

namespace RageRoom
{
    /// <summary>
    /// Ordre de passage des morceaux d'une radio. Ne connaît que des numéros de morceaux (0 à count - 1) :
    /// c'est le RadioPlayer qui fait le lien avec les fichiers audio.
    /// En mode aléatoire, tous les morceaux passent une fois avant qu'un seul ne repasse,
    /// et le même morceau ne passe jamais deux fois de suite.
    /// </summary>
    public sealed class RadioPlaylist
    {
        readonly int count;
        readonly bool shuffle;
        readonly Random random;
        readonly int[] order;
        int position = -1;

        public RadioPlaylist(int count, bool shuffle, int seed)
        {
            this.count = Math.Max(0, count);
            this.shuffle = shuffle;
            random = new Random(seed);
            order = new int[this.count];
            for (int i = 0; i < this.count; i++) order[i] = i;
            if (shuffle) Shuffle();
        }

        /// <summary>Nombre de morceaux.</summary>
        public int Count { get { return count; } }

        /// <summary>Numéro du morceau en cours, ou -1 si rien n'a encore été joué.</summary>
        public int Current { get { return position >= 0 && position < count ? order[position] : -1; } }

        /// <summary>Passe au morceau suivant et renvoie son numéro (-1 si la radio est vide).</summary>
        public int Next()
        {
            if (count == 0) return -1;

            position++;
            if (position >= count)
            {
                // Tour terminé : on remélange, sans rejouer tout de suite le morceau qui vient de finir.
                position = 0;
                if (shuffle && count > 1)
                {
                    int last = order[count - 1];
                    Shuffle();
                    if (order[0] == last)
                    {
                        int swap = 1 + random.Next(count - 1);
                        order[0] = order[swap];
                        order[swap] = last;
                    }
                }
            }
            return order[position];
        }

        /// <summary>Revient au morceau d'avant et renvoie son numéro (-1 si la radio est vide).</summary>
        public int Previous()
        {
            if (count == 0) return -1;
            position = position <= 0 ? count - 1 : position - 1;
            return order[position];
        }

        void Shuffle()
        {
            for (int i = count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                int tmp = order[i];
                order[i] = order[j];
                order[j] = tmp;
            }
        }
    }
}
