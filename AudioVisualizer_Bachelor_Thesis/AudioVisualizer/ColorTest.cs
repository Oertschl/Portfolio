using UnityEngine;

public class ColorTest : MonoBehaviour
{
    private ParticleSystemRenderer psRenderer;

    void Start()
    {
        // Find MusicWave_Sorting child
        var ps = GetComponentInChildren<ParticleSystem>();
        psRenderer = ps.GetComponent<ParticleSystemRenderer>();

        Debug.Log($"[ColorTest] Found renderer: {psRenderer != null}, " +
                  $"material: {(psRenderer != null ? psRenderer.material.name : "null")}, " +
                  $"shader: {(psRenderer != null ? psRenderer.material.shader.name : "null")}");
    }

    void Update()
    {
        // On spacebar: force bright red directly onto every possible color property
        if (Input.GetKeyDown(KeyCode.Space))
        {
            if (psRenderer != null)
            {
                var mat = psRenderer.material;
                mat.SetColor("_BaseColor", Color.red);
                mat.SetColor("_Color", Color.red);
                mat.color = Color.red;

                var ps = psRenderer.GetComponent<ParticleSystem>();
                var main = ps.main;
                main.startColor = new ParticleSystem.MinMaxGradient(Color.red);
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.Play();

                Debug.Log($"[ColorTest] Forced red. _BaseColor={mat.GetColor("_BaseColor")}, " +
                          $"shader keywords={string.Join(',', mat.shaderKeywords)}");
            }
        }
    }
}