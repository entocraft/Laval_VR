using UnityEngine;

public class Teleportation : MonoBehaviour
{
    public Transform player, destination;
    public GameObject playerg;

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            playerg.SetActive(false);
            player.position = destination.position;
            playerg.SetActive(true);
        }
        
    }
}
