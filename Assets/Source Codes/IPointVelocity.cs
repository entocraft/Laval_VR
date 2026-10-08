using UnityEngine;

/// <summary>
/// Objet capable de donner la vitesse réelle d'un de ses points, même quand il est tenu en main (kinematic),
/// cas où le moteur physique ne la connaît pas. Implémenté par les scripts de casse, d'écrasement, de sons et d'armes.
///
/// Ce fichier n'est pas un composant : il n'y a rien à ajouter sur un objet, il doit seulement être dans le projet.
/// </summary>
public interface IPointVelocity
{
    Vector3 PointVelocity(Vector3 worldPoint);
}