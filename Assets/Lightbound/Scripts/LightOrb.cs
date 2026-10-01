using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Thrown light. Destroys anything tagged "Barrier", stuns enemies it passes through,
/// and when it lands it becomes a lure that enemies walk/fly toward while its light shrinks away.
/// </summary>
public class LightOrb : MonoBehaviour
{
    public static readonly List<LightOrb> Lures = new List<LightOrb>();

    public Transform lightMask;          // child with a hard-edged Point Light 2D
    public float lureTime = 6f;
    public float stunTime = 2f;
    public float maxFlightTime = 2.5f;   // hangs in the air as a lure if it never hits anything

    public bool IsLure { get; private set; }
    float _landedAt, _thrownAt;
    Light2D _light;
    float _startRadius;

    void Awake()
    {
        _thrownAt = Time.time;
        if (lightMask != null) _light = lightMask.GetComponent<Light2D>();
        if (_light != null) _startRadius = _light.pointLightOuterRadius;
    }

    void OnDisable() => Lures.Remove(this);

    void OnCollisionEnter2D(Collision2D hit)
    {
        if (IsLure) return;
        if (hit.gameObject.CompareTag("Barrier"))
        {
            Destroy(hit.gameObject);
            Destroy(gameObject);
            return;
        }
        Land();
    }

    void Land()
    {
        var rb = GetComponent<Rigidbody2D>();
        rb.linearVelocity = Vector2.zero;
        rb.bodyType = RigidbodyType2D.Kinematic;
        IsLure = true;
        _landedAt = Time.time;
        Lures.Add(this);
        Destroy(gameObject, lureTime);
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        var enemy = other.GetComponentInParent<Enemy>();
        if (enemy != null) enemy.Stun(stunTime);
    }

    void Update()
    {
        if (!IsLure && Time.time - _thrownAt > maxFlightTime) Land();
        if (IsLure && _light != null)
        {
            float r = _startRadius * Mathf.Max(0f, 1f - (Time.time - _landedAt) / lureTime);
            _light.pointLightOuterRadius = r;
            _light.pointLightInnerRadius = r * 0.97f;
        }
    }
}
