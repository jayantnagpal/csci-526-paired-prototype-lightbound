using UnityEngine;

/// <summary>
/// Moves toward light. Lures (landed light orbs) pull strongly; the player's own light pulls slightly,
/// and only while the player still has light and is inside its radius.
/// Crawler: walks, turns at walls and ledges. Flyer: floats freely (kinematic body).
/// The child "Hurtbox" trigger (tag Enemy) is what hurts the player; it's switched off while stunned.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public class Enemy : MonoBehaviour
{
    public bool flyer;
    public float patrolSpeed = 1.5f;
    public float playerPullSpeed = 2.2f;   // slight attraction, well below player speed
    public float lureSpeed = 3.5f;
    public float lureRange = 10f;
    public LayerMask groundMask;
    public Collider2D hurtbox;

    Rigidbody2D _rb;
    Player _player;
    Vector2 _home;
    int _dir = 1;
    float _stunnedUntil;

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _home = transform.position;
        var p = GameObject.FindWithTag("Player");
        if (p != null) _player = p.GetComponent<Player>();
    }

    public void Stun(float seconds) => _stunnedUntil = Time.time + seconds;

    void FixedUpdate()
    {
        bool stunned = Time.time < _stunnedUntil;
        if (hurtbox != null) hurtbox.enabled = !stunned;
        if (stunned) { _rb.linearVelocity = flyer ? Vector2.zero : new Vector2(0f, _rb.linearVelocity.y); return; }

        Vector2 pos = transform.position;
        Vector2? target = null;
        float speed = patrolSpeed;

        // strongest pull: nearest lure
        float best = lureRange;
        foreach (var lure in LightOrb.Lures)
        {
            float d = Vector2.Distance(pos, lure.transform.position);
            if (lure.IsLure && d < best) { best = d; target = lure.transform.position; speed = lureSpeed; }
        }
        // slight pull: the player's light
        if (target == null && _player != null && _player.HasLight &&
            Vector2.Distance(pos, _player.LightCenter) < _player.LightRadius + 1.5f) // noticed from where the light is, not the body
        {
            target = _player.transform.position;
            speed = playerPullSpeed;
        }

        if (flyer) Fly(pos, target, speed);
        else Crawl(pos, target, speed);
    }

    void Crawl(Vector2 pos, Vector2? target, float speed)
    {
        int want = _dir;
        if (target.HasValue) want = Mathf.Abs(target.Value.x - pos.x) < 0.3f ? 0 : (int)Mathf.Sign(target.Value.x - pos.x);

        if (want != 0)
        {
            bool groundAhead = Physics2D.Raycast(pos + new Vector2(want * 0.5f, 0f), Vector2.down, 1f, groundMask);
            bool wallAhead = Physics2D.Raycast(pos, new Vector2(want, 0f), 0.6f, groundMask);
            if (!groundAhead || wallAhead)
            {
                if (target.HasValue) want = 0;      // wait at the edge, staring at the light
                else { _dir = -_dir; want = _dir; }
            }
        }
        if (want != 0) _dir = want;
        _rb.linearVelocity = new Vector2(want * speed, _rb.linearVelocity.y);
    }

    void Fly(Vector2 pos, Vector2? target, float speed)
    {
        Vector2 goal = target ?? _home + new Vector2(Mathf.Sin(Time.time * 0.5f) * 2.5f, 0f);
        _rb.MovePosition(Vector2.MoveTowards(pos, goal, speed * Time.fixedDeltaTime));
    }
}
