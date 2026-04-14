using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using GHelper.Linux.I18n;

namespace GHelper.Linux.UI.Views;

/// <summary>
/// ROG Fighter — retro top-down arcade shooter (1942-style).
/// Pure wave-based formation spawning, 4 enemy types, boss every 5 waves,
/// 6 power-ups, high score persistence. All Canvas rectangles, no image assets.
/// </summary>
public partial class ArcadeWindow : Window
{
    // ── Constants ──
    const double CW = 480, CH = 640;
    const double PlayerSpeed = 5, BulletSpeed = 7, EnemyBulletSpeed = 3.5;
    const int StarCount = 50;

    // ── Enemy types ──
    enum EnemyType { Scout, Fighter, Bomber, Ace, Boss }

    // ── Colors ──
    static readonly IBrush PlayerBody = new SolidColorBrush(Color.Parse("#FFD700"));
    static readonly IBrush PlayerWing = new SolidColorBrush(Color.Parse("#DAA520"));
    static readonly IBrush PlayerCockpit = new SolidColorBrush(Color.Parse("#B8860B"));
    static readonly IBrush PlayerEngine = new SolidColorBrush(Color.Parse("#FF8C00"));

    static readonly IBrush ScoutBody = new SolidColorBrush(Color.Parse("#5DADE2"));
    static readonly IBrush ScoutAccent = new SolidColorBrush(Color.Parse("#2E86C1"));
    static readonly IBrush FighterBody = new SolidColorBrush(Color.Parse("#FF4444"));
    static readonly IBrush FighterAccent = new SolidColorBrush(Color.Parse("#CC0000"));
    static readonly IBrush BomberBody = new SolidColorBrush(Color.Parse("#27AE60"));
    static readonly IBrush BomberAccent = new SolidColorBrush(Color.Parse("#1E8449"));
    static readonly IBrush AceBody = new SolidColorBrush(Color.Parse("#F1C40F"));
    static readonly IBrush AceAccent = new SolidColorBrush(Color.Parse("#D4AC0D"));
    // Boss 0: Mothership (purple)
    static readonly IBrush Boss0Body = new SolidColorBrush(Color.Parse("#9B59B6"));
    static readonly IBrush Boss0Accent = new SolidColorBrush(Color.Parse("#7D3C98"));
    static readonly IBrush Boss0Detail = new SolidColorBrush(Color.Parse("#C39BD3"));
    // Boss 1: Fortress (dark red)
    static readonly IBrush Boss1Body = new SolidColorBrush(Color.Parse("#C0392B"));
    static readonly IBrush Boss1Accent = new SolidColorBrush(Color.Parse("#922B21"));
    static readonly IBrush Boss1Detail = new SolidColorBrush(Color.Parse("#E74C3C"));
    // Boss 2: Phantom (teal)
    static readonly IBrush Boss2Body = new SolidColorBrush(Color.Parse("#1ABC9C"));
    static readonly IBrush Boss2Accent = new SolidColorBrush(Color.Parse("#148F77"));
    static readonly IBrush Boss2Detail = new SolidColorBrush(Color.Parse("#76D7C4"));
    // Boss 3: Bombardier (dark orange)
    static readonly IBrush Boss3Body = new SolidColorBrush(Color.Parse("#D35400"));
    static readonly IBrush Boss3Accent = new SolidColorBrush(Color.Parse("#A04000"));
    static readonly IBrush Boss3Detail = new SolidColorBrush(Color.Parse("#E67E22"));
    // Boss 4: Overlord (crimson + gold)
    static readonly IBrush Boss4Body = new SolidColorBrush(Color.Parse("#8E44AD"));
    static readonly IBrush Boss4Accent = new SolidColorBrush(Color.Parse("#6C3483"));
    static readonly IBrush Boss4Crown = new SolidColorBrush(Color.Parse("#F1C40F"));

    static readonly IBrush BulletBrush = new SolidColorBrush(Color.Parse("#4CC2FF"));
    static readonly IBrush EnemyBulletBrush = new SolidColorBrush(Color.Parse("#FF6666"));
    static readonly IBrush SpreadColor = new SolidColorBrush(Color.Parse("#2ECC71"));
    static readonly IBrush ShieldColor = new SolidColorBrush(Color.Parse("#3498DB"));
    static readonly IBrush RapidColor = new SolidColorBrush(Color.Parse("#E67E22"));
    static readonly IBrush BombColor = new SolidColorBrush(Color.Parse("#E74C3C"));
    static readonly IBrush MagnetColor = new SolidColorBrush(Color.Parse("#F1C40F"));
    static readonly IBrush LifeColor = new SolidColorBrush(Color.Parse("#E91E8F"));
    static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#F0F0F0"));
    static readonly IBrush DimBrush = new SolidColorBrush(Color.Parse("#888888"));

    // ── Data types ──
    enum GameState { Menu, Playing, GameOver }
    enum PowerUpType { Spread, Shield, Rapid, Bomb, Magnet, Life }

    record struct Bullet(double X, double Y, double Dx, double Dy, bool IsPlayer);
    record struct Enemy(double X, double Y, double Dx, double Dy, int Hp, int MaxHp,
                        EnemyType Type, int ShootCd, double SpawnParam, int BossId);
    record struct PowerUp(double X, double Y, PowerUpType Type);
    record struct Star(double X, double Y, double Speed, double Brightness);

    // ── Game state ──
    GameState _state = GameState.Menu;
    double _playerX, _playerY;
    int _score, _highScore, _lives, _frame;
    bool _keyLeft, _keyRight, _keyUp, _keyDown, _keyShoot;
    int _fireCooldown, _spreadTimer, _rapidTimer, _magnetTimer;
    bool _hasShield;

    // Wave system
    int _waveNumber;
    int _waveSpawnIndex;    // how many enemies spawned in current wave
    int _waveSpawnTotal;    // total enemies in current wave
    int _waveSpawnCd;       // frames between spawns within a wave
    int _wavePauseCd;       // frames of pause between waves
    EnemyType _waveType;
    bool _waveActive;

    readonly List<Bullet> _bullets = new();
    readonly List<Enemy> _enemies = new();
    readonly List<PowerUp> _powerUps = new();
    readonly Star[] _stars = new Star[StarCount];
    readonly DispatcherTimer _gameTimer;
    readonly Random _rng = new();

    public ArcadeWindow()
    {
        InitializeComponent();
        _highScore = Helpers.AppConfig.Get("arcade_highscore", 0);
        _gameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _gameTimer.Tick += (_, _) => GameTick();
        InitStars();
        Loaded += (_, _) => { gameCanvas.Focus(); _gameTimer.Start(); };
        Closing += (_, _) => _gameTimer.Stop();
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
    }

    // ── Input ──

    private void OnKeyDown(object? s, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left or Key.A: _keyLeft = true; break;
            case Key.Right or Key.D: _keyRight = true; break;
            case Key.Up or Key.W: _keyUp = true; break;
            case Key.Down or Key.S: _keyDown = true; break;
            case Key.Space: _keyShoot = true; break;
            case Key.Return or Key.Enter:
                if (_state != GameState.Playing) StartGame(); break;
            case Key.Escape: Close(); break;
        }
    }

    private void OnKeyUp(object? s, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left or Key.A: _keyLeft = false; break;
            case Key.Right or Key.D: _keyRight = false; break;
            case Key.Up or Key.W: _keyUp = false; break;
            case Key.Down or Key.S: _keyDown = false; break;
            case Key.Space: _keyShoot = false; break;
        }
    }

    // ── Lifecycle ──

    private void StartGame()
    {
        _state = GameState.Playing;
        _playerX = CW / 2; _playerY = CH - 60;
        _score = 0; _lives = 3; _frame = 0;
        _fireCooldown = 0; _spreadTimer = 0; _rapidTimer = 0; _magnetTimer = 0;
        _hasShield = false;
        _waveNumber = 0; _waveActive = false; _wavePauseCd = 60;
        _bullets.Clear(); _enemies.Clear(); _powerUps.Clear();
    }

    private void InitStars()
    {
        for (int i = 0; i < StarCount; i++)
            _stars[i] = new Star(_rng.NextDouble() * CW, _rng.NextDouble() * CH,
                0.3 + _rng.NextDouble() * 1.5, 0.3 + _rng.NextDouble() * 0.7);
    }

    // ── Game loop ──

    private void GameTick()
    {
        _frame++;
        UpdateStars();
        if (_state == GameState.Playing)
        {
            UpdatePlayer();
            UpdateBullets();
            UpdateEnemies();
            UpdatePowerUps();
            RunWaveSpawner();
            CheckCollisions();
        }
        Render();
    }

    private void UpdateStars()
    {
        for (int i = 0; i < StarCount; i++)
        {
            var st = _stars[i];
            st.Y += st.Speed;
            if (st.Y > CH) { st.Y = 0; st.X = _rng.NextDouble() * CW; }
            _stars[i] = st;
        }
    }

    private void UpdatePlayer()
    {
        if (_keyLeft) _playerX = Math.Max(20, _playerX - PlayerSpeed);
        if (_keyRight) _playerX = Math.Min(CW - 20, _playerX + PlayerSpeed);
        if (_keyUp) _playerY = Math.Max(CH * 0.35, _playerY - PlayerSpeed);
        if (_keyDown) _playerY = Math.Min(CH - 30, _playerY + PlayerSpeed);

        if (_fireCooldown > 0) _fireCooldown--;
        int rate = _rapidTimer > 0 ? 5 : 10;
        if (_keyShoot && _fireCooldown <= 0)
        {
            _fireCooldown = rate;
            if (_spreadTimer > 0)
            {
                _bullets.Add(new Bullet(_playerX, _playerY - 16, 0, -BulletSpeed, true));
                _bullets.Add(new Bullet(_playerX - 10, _playerY - 12, -1.2, -BulletSpeed, true));
                _bullets.Add(new Bullet(_playerX + 10, _playerY - 12, 1.2, -BulletSpeed, true));
            }
            else
            {
                _bullets.Add(new Bullet(_playerX, _playerY - 16, 0, -BulletSpeed, true));
            }
        }
        if (_spreadTimer > 0) _spreadTimer--;
        if (_rapidTimer > 0) _rapidTimer--;
        if (_magnetTimer > 0) _magnetTimer--;
    }

    private void UpdateBullets()
    {
        for (int i = _bullets.Count - 1; i >= 0; i--)
        {
            var b = _bullets[i] with { X = _bullets[i].X + _bullets[i].Dx, Y = _bullets[i].Y + _bullets[i].Dy };
            if (b.Y < -10 || b.Y > CH + 10 || b.X < -10 || b.X > CW + 10)
                _bullets.RemoveAt(i);
            else _bullets[i] = b;
        }
    }

    private void UpdateEnemies()
    {
        for (int i = _enemies.Count - 1; i >= 0; i--)
        {
            var e = _enemies[i];
            double dx = e.Dx, dy = e.Dy;

            // Movement by type
            switch (e.Type)
            {
                case EnemyType.Boss when e.Y > 70:
                    dy = 0;
                    dx = e.BossId switch
                    {
                        2 => 0, // Phantom: stationary until teleport
                        _ => Math.Sin(_frame * 0.02) * 2.5
                    };
                    // Phantom teleport every ~3 seconds
                    if (e.BossId == 2 && _frame % 180 == 0)
                    {
                        double newX = 80 + _rng.NextDouble() * (CW - 160);
                        e = e with { X = newX };
                    }
                    break;
                case EnemyType.Ace:
                    dx = Math.Sin((_frame + e.SpawnParam) * 0.06) * 2.5;
                    break;
                case EnemyType.Fighter when e.Y > 50:
                    dx = Math.Sin((_frame + e.SpawnParam) * 0.04) * 1.8;
                    break;
            }

            e = e with { X = e.X + dx, Y = e.Y + dy, Dx = dx, Dy = dy };

            // Shooting
            int shootCd = e.ShootCd > 0 ? e.ShootCd - 1 : 0;
            if (shootCd <= 0 && e.Y > 30 && e.Y < CH * 0.65)
            {
                if (e.Type == EnemyType.Boss && e.Dy == 0)
                {
                    // Boss-specific attack patterns
                    bool fire = _rng.Next(100) < 5;
                    if (fire)
                    {
                        switch (e.BossId)
                        {
                            case 0: // Mothership: 3-spread + spawn scouts
                                _bullets.Add(new Bullet(e.X - 20, e.Y + 30, -1.2, EnemyBulletSpeed, false));
                                _bullets.Add(new Bullet(e.X, e.Y + 30, 0, EnemyBulletSpeed + 0.5, false));
                                _bullets.Add(new Bullet(e.X + 20, e.Y + 30, 1.2, EnemyBulletSpeed, false));
                                if (_frame % 120 < 2) // spawn escorts periodically
                                {
                                    _enemies.Add(new Enemy(e.X - 40, e.Y, -0.5, 1.5, 1, 1, EnemyType.Scout, 0, _frame, 0));
                                    _enemies.Add(new Enemy(e.X + 40, e.Y, 0.5, 1.5, 1, 1, EnemyType.Scout, 0, _frame, 0));
                                }
                                shootCd = 35;
                                break;
                            case 1: // Fortress: alternating turret bursts
                                if (_frame % 50 < 25)
                                {
                                    _bullets.Add(new Bullet(e.X - 30, e.Y + 10, -0.8, EnemyBulletSpeed, false));
                                    _bullets.Add(new Bullet(e.X - 30, e.Y + 10, -1.6, EnemyBulletSpeed * 0.8, false));
                                }
                                else
                                {
                                    _bullets.Add(new Bullet(e.X + 30, e.Y + 10, 0.8, EnemyBulletSpeed, false));
                                    _bullets.Add(new Bullet(e.X + 30, e.Y + 10, 1.6, EnemyBulletSpeed * 0.8, false));
                                }
                                shootCd = 25;
                                break;
                            case 2: // Phantom: aimed shot toward player
                            {
                                double adx = _playerX - e.X, ady = _playerY - e.Y;
                                double dist = Math.Sqrt(adx * adx + ady * ady);
                                if (dist > 1)
                                {
                                    _bullets.Add(new Bullet(e.X, e.Y + 20,
                                        adx / dist * EnemyBulletSpeed * 0.8,
                                        ady / dist * EnemyBulletSpeed * 0.8, false));
                                }
                                shootCd = 40;
                                break;
                            }
                            case 3: // Bombardier: 5-bomb carpet
                                for (int b = 0; b < 5; b++)
                                    _bullets.Add(new Bullet(e.X - 30 + b * 15, e.Y + 25, 0, 2.0, false));
                                shootCd = 50;
                                break;
                            case 4: // Overlord: 5-spread + aimed alternating
                                if (_frame % 60 < 30)
                                {
                                    for (int b = 0; b < 5; b++)
                                        _bullets.Add(new Bullet(e.X, e.Y + 30,
                                            (b - 2) * 1.0, EnemyBulletSpeed, false));
                                }
                                else
                                {
                                    double adx2 = _playerX - e.X, ady2 = _playerY - e.Y;
                                    double d2 = Math.Sqrt(adx2 * adx2 + ady2 * ady2);
                                    if (d2 > 1)
                                        _bullets.Add(new Bullet(e.X, e.Y + 20,
                                            adx2 / d2 * EnemyBulletSpeed,
                                            ady2 / d2 * EnemyBulletSpeed, false));
                                }
                                shootCd = 30;
                                break;
                        }
                    }
                }
                else if (e.Type is EnemyType.Bomber or EnemyType.Ace)
                {
                    bool fire = e.Type == EnemyType.Bomber ? _rng.Next(100) < 3 : _rng.Next(100) < 2;
                    if (fire)
                    {
                        _bullets.Add(new Bullet(e.X, e.Y + 12, 0, EnemyBulletSpeed, false));
                        shootCd = e.Type == EnemyType.Bomber ? 50 : 70;
                    }
                }
            }
            e = e with { ShootCd = shootCd };

            if (e.Y > CH + 60 || e.X < -100 || e.X > CW + 100)
                _enemies.RemoveAt(i);
            else _enemies[i] = e;
        }
    }

    private void UpdatePowerUps()
    {
        for (int i = _powerUps.Count - 1; i >= 0; i--)
        {
            var p = _powerUps[i];
            double dy = 1.5;
            double dx = 0;

            // Magnet: power-ups fly toward player
            if (_magnetTimer > 0)
            {
                double ddx = _playerX - p.X, ddy = _playerY - p.Y;
                double dist = Math.Sqrt(ddx * ddx + ddy * ddy);
                if (dist > 5) { dx = ddx / dist * 4; dy = ddy / dist * 4; }
            }

            p = p with { X = p.X + dx, Y = p.Y + dy };
            if (p.Y > CH + 20) _powerUps.RemoveAt(i);
            else _powerUps[i] = p;
        }
    }

    // ── Wave spawner ──

    private void RunWaveSpawner()
    {
        if (!_waveActive)
        {
            // Progressive threshold: starts at 50%, reaches 20% by wave 15
            double threshold = CH * Math.Max(0.2, 0.5 - _waveNumber * 0.02);
            foreach (var e in _enemies)
                if (e.Y < threshold) return;

            if (_wavePauseCd > 0) { _wavePauseCd--; return; }

            // Start next wave
            _waveNumber++;
            _waveSpawnIndex = 0;
            _waveSpawnCd = 0;
            _waveActive = true;

            // Boss every 5 waves
            if (_waveNumber % 5 == 0)
            {
                _waveType = EnemyType.Boss;
                _waveSpawnTotal = 1;
            }
            else
            {
                // Cycle through types, introducing harder ones as waves progress
                int cycle = ((_waveNumber - 1) % 4);
                _waveType = _waveNumber switch
                {
                    <= 2 => EnemyType.Scout,
                    <= 4 => cycle == 0 ? EnemyType.Scout : EnemyType.Fighter,
                    <= 8 => cycle switch { 0 => EnemyType.Scout, 1 => EnemyType.Fighter,
                                           2 => EnemyType.Bomber, _ => EnemyType.Fighter },
                    _ => cycle switch { 0 => EnemyType.Fighter, 1 => EnemyType.Bomber,
                                        2 => EnemyType.Ace, _ => EnemyType.Scout }
                };
                _waveSpawnTotal = _waveType switch
                {
                    EnemyType.Scout => 6,
                    EnemyType.Fighter => 5,
                    EnemyType.Bomber => 4,
                    EnemyType.Ace => 4,
                    _ => 5
                };
            }
        }

        // Spawn enemies within the wave
        if (_waveSpawnCd > 0) { _waveSpawnCd--; return; }
        if (_waveSpawnIndex >= _waveSpawnTotal) { _waveActive = false; _wavePauseCd = 30; return; }

        SpawnWaveEnemy(_waveType, _waveSpawnIndex, _waveSpawnTotal);
        _waveSpawnIndex++;
        _waveSpawnCd = _waveType == EnemyType.Boss ? 0 : 8;
    }

    private void SpawnWaveEnemy(EnemyType type, int idx, int total)
    {
        int cycle = _waveNumber / 5; // difficulty multiplier
        double speedMul = 1.0 + cycle * 0.15;

        switch (type)
        {
            case EnemyType.Scout:
            {
                // V-formation
                double cx = CW / 2;
                double offsetX = (idx - total / 2.0) * 40;
                double offsetY = Math.Abs(idx - total / 2.0) * 20;
                _enemies.Add(new Enemy(cx + offsetX, -20 - offsetY, 0, 1.8 * speedMul,
                    1, 1, EnemyType.Scout, 0, idx * 50, 0));
                break;
            }
            case EnemyType.Fighter:
            {
                double x = 60 + idx * ((CW - 120) / Math.Max(total - 1, 1));
                _enemies.Add(new Enemy(x, -20 - idx * 10, 0, 1.4 * speedMul,
                    1, 1, EnemyType.Fighter, 0, idx * 80, 0));
                break;
            }
            case EnemyType.Bomber:
            {
                double x = CW * 0.3 + idx * (CW * 0.2);
                _enemies.Add(new Enemy(x, -30 - idx * 40, 0, 0.8 * speedMul,
                    2 + cycle, 2 + cycle, EnemyType.Bomber, 0, idx * 60, 0));
                break;
            }
            case EnemyType.Ace:
            {
                double cx = CW / 2;
                double offsetX = (idx - total / 2.0) * 50;
                _enemies.Add(new Enemy(cx + offsetX, -20, 0, 2.0 * speedMul,
                    2 + cycle, 2 + cycle, EnemyType.Ace, 0, idx * 100, 0));
                break;
            }
            case EnemyType.Boss:
            {
                int bossId = ((_waveNumber / 5) - 1) % 5;
                int hp = bossId switch
                {
                    0 => 20, // Mothership
                    1 => 25, // Fortress
                    2 => 20, // Phantom
                    3 => 30, // Bombardier
                    4 => 35, // Overlord
                    _ => 20
                } + cycle * 8;
                _enemies.Add(new Enemy(CW / 2, -60, 0, 0.8, hp, hp, EnemyType.Boss, 0, 0, bossId));
                break;
            }
        }
    }

    // ── Collisions ──

    private void CheckCollisions()
    {
        // Player bullets → enemies
        for (int bi = _bullets.Count - 1; bi >= 0; bi--)
        {
            if (!_bullets[bi].IsPlayer) continue;
            var b = _bullets[bi];

            for (int ei = _enemies.Count - 1; ei >= 0; ei--)
            {
                var e = _enemies[ei];
                var (ew, eh) = EnemySize(e.Type);

                if (HitsRect(b.X, b.Y, e.X, e.Y, ew, eh))
                {
                    _bullets.RemoveAt(bi);
                    e = e with { Hp = e.Hp - 1 };

                    if (e.Hp <= 0)
                    {
                        _enemies.RemoveAt(ei);
                        _score += e.Type switch
                        {
                            EnemyType.Boss => 50,
                            EnemyType.Ace => 30,
                            EnemyType.Bomber => 20,
                            _ => 10
                        };
                        try { Helpers.CoinSound.Play(); } catch { }

                        // Power-up drop: 15% normal, 100% boss
                        if (e.Type == EnemyType.Boss || _rng.Next(100) < 15)
                        {
                            var pt = (PowerUpType)_rng.Next(6);
                            _powerUps.Add(new PowerUp(e.X, e.Y, pt));
                        }
                    }
                    else _enemies[ei] = e;
                    break;
                }
            }
        }

        // Enemy bullets → player
        for (int bi = _bullets.Count - 1; bi >= 0; bi--)
        {
            if (_bullets[bi].IsPlayer) continue;
            if (HitsRect(_bullets[bi].X, _bullets[bi].Y, _playerX, _playerY, 28, 28))
            {
                _bullets.RemoveAt(bi);
                HitPlayer();
            }
        }

        // Enemies → player body collision
        for (int ei = _enemies.Count - 1; ei >= 0; ei--)
        {
            var e = _enemies[ei];
            var (ew, eh) = EnemySize(e.Type);
            if (RectsOverlap(_playerX, _playerY, 28, 28, e.X, e.Y, ew, eh))
            {
                if (e.Type != EnemyType.Boss) _enemies.RemoveAt(ei);
                HitPlayer();
            }
        }

        // Power-ups → player
        for (int i = _powerUps.Count - 1; i >= 0; i--)
        {
            var p = _powerUps[i];
            if (HitsRect(p.X, p.Y, _playerX, _playerY, 36, 36))
            {
                _powerUps.RemoveAt(i);
                ApplyPowerUp(p.Type);
            }
        }
    }

    private void ApplyPowerUp(PowerUpType type)
    {
        switch (type)
        {
            case PowerUpType.Spread: _spreadTimer = 600; break;
            case PowerUpType.Rapid: _rapidTimer = 600; break;
            case PowerUpType.Shield: _hasShield = true; break;
            case PowerUpType.Bomb:
                // Clear all non-boss enemies, damage boss 5HP
                for (int i = _enemies.Count - 1; i >= 0; i--)
                {
                    if (_enemies[i].Type == EnemyType.Boss)
                    {
                        var boss = _enemies[i] with { Hp = _enemies[i].Hp - 5 };
                        if (boss.Hp <= 0) { _enemies.RemoveAt(i); _score += 50; }
                        else _enemies[i] = boss;
                    }
                    else
                    {
                        _score += 10;
                        _enemies.RemoveAt(i);
                    }
                }
                try { Helpers.CoinSound.Play(); } catch { }
                break;
            case PowerUpType.Magnet: _magnetTimer = 480; break; // 8 sec
            case PowerUpType.Life:
                if (_lives < 5) _lives++;
                break;
        }
    }

    private void HitPlayer()
    {
        if (_hasShield) { _hasShield = false; return; }
        _lives--;
        _spreadTimer = 0; _rapidTimer = 0;
        if (_lives <= 0)
        {
            _state = GameState.GameOver;
            if (_score > _highScore)
            {
                _highScore = _score;
                Helpers.AppConfig.Set("arcade_highscore", _highScore);
            }
        }
    }

    static (double w, double h) EnemySize(EnemyType t) => t switch
    {
        EnemyType.Scout => (24, 16),
        EnemyType.Fighter => (36, 22),
        EnemyType.Bomber => (38, 26),
        EnemyType.Ace => (32, 20),
        EnemyType.Boss => (74, 48),
        _ => (24, 20)
    };

    static bool HitsRect(double px, double py, double rx, double ry, double rw, double rh)
        => px > rx - rw / 2 && px < rx + rw / 2 && py > ry - rh / 2 && py < ry + rh / 2;

    static bool RectsOverlap(double x1, double y1, double w1, double h1,
                              double x2, double y2, double w2, double h2)
        => Math.Abs(x1 - x2) < (w1 + w2) / 2 && Math.Abs(y1 - y2) < (h1 + h2) / 2;

    // ══════════════════ RENDERING ══════════════════

    private void Render()
    {
        gameCanvas.Children.Clear();

        // Starfield
        foreach (var s in _stars)
        {
            var star = new Rectangle { Width = 2, Height = 2,
                Fill = new SolidColorBrush(Color.FromArgb((byte)(s.Brightness * 255), 255, 255, 255)) };
            Canvas.SetLeft(star, s.X); Canvas.SetTop(star, s.Y);
            gameCanvas.Children.Add(star);
        }

        if (_state == GameState.Menu)
        {
            DrawText(Labels.Get("arcade_game_title"), CW / 2, 160, 28, TextBrush, true);
            DrawText(Labels.Get("arcade_move"), CW / 2, 270, 13, DimBrush, true);
            DrawText(Labels.Get("arcade_shoot"), CW / 2, 292, 13, DimBrush, true);
            DrawText(Labels.Get("arcade_start"), CW / 2, 330, 16, TextBrush, true);
            DrawText(Labels.Get("arcade_quit"), CW / 2, 358, 12, DimBrush, true);
            // Draw player plane on menu as preview
            DrawPlayerPlane(CW / 2, 220);
            if (_highScore > 0)
                DrawText(Labels.Format("arcade_highscore", _highScore), CW / 2, 420, 14, PlayerBody, true);
            return;
        }

        // Power-ups
        foreach (var p in _powerUps)
        {
            var (brush, label) = PuVisual(p.Type);
            DrawRect(p.X, p.Y, 16, 16, brush);
            DrawText(label, p.X, p.Y - 1, 9, TextBrush, true);
        }

        // Bullets
        foreach (var b in _bullets)
            DrawRect(b.X, b.Y, 3, 8, b.IsPlayer ? BulletBrush : EnemyBulletBrush);

        // Enemies
        foreach (var e in _enemies) DrawEnemy(e);

        // Player
        if (_state == GameState.Playing)
        {
            DrawPlayerPlane(_playerX, _playerY);
            if (_hasShield)
            {
                var sh = new Ellipse { Width = 44, Height = 44,
                    Stroke = ShieldColor, StrokeThickness = 2, Fill = null };
                Canvas.SetLeft(sh, _playerX - 22); Canvas.SetTop(sh, _playerY - 22);
                gameCanvas.Children.Add(sh);
            }
        }

        // HUD
        DrawText(Labels.Format("arcade_score", _score), 10, 10, 14, TextBrush, false);
        DrawText(Labels.Format("arcade_high", _highScore), CW - 10, 10, 12, DimBrush, false, true);
        DrawText(Labels.Format("arcade_wave", _waveNumber), CW / 2, 10, 12, DimBrush, true);
        for (int i = 0; i < _lives; i++)
            DrawRect(CW / 2 - 24 + i * 14, 30, 8, 8, PlayerBody);

        double iy = 48;
        if (_spreadTimer > 0) { DrawText(Labels.Format("arcade_spread", _spreadTimer / 60), 10, iy, 10, SpreadColor, false); iy += 13; }
        if (_rapidTimer > 0) { DrawText(Labels.Format("arcade_rapid", _rapidTimer / 60), 10, iy, 10, RapidColor, false); iy += 13; }
        if (_magnetTimer > 0) { DrawText(Labels.Format("arcade_magnet", _magnetTimer / 60), 10, iy, 10, MagnetColor, false); iy += 13; }
        if (_hasShield) { DrawText(Labels.Get("arcade_shield"), 10, iy, 10, ShieldColor, false); }

        // Game over
        if (_state == GameState.GameOver)
        {
            DrawRect(CW / 2, CH / 2, CW, 160, new SolidColorBrush(Color.FromArgb(180, 10, 10, 10)));
            DrawText(Labels.Get("arcade_game_over"), CW / 2, CH / 2 - 30, 28, FighterBody, true);
            DrawText(Labels.Format("arcade_score_wave", _score, _waveNumber), CW / 2, CH / 2 + 10, 16, TextBrush, true);
            if (_score >= _highScore && _score > 0)
                DrawText(Labels.Get("arcade_new_highscore"), CW / 2, CH / 2 + 36, 14, PlayerBody, true);
            DrawText(Labels.Get("arcade_retry"), CW / 2, CH / 2 + 62, 14, DimBrush, true);
        }
    }

    // ── Plane drawing ──

    private void DrawPlayerPlane(double x, double y)
    {
        // Fuselage
        DrawRect(x, y, 10, 28, PlayerBody);
        // Cockpit (top)
        DrawRect(x, y - 12, 6, 8, PlayerCockpit);
        // Nose
        DrawRect(x, y - 18, 4, 6, PlayerWing);
        // Main wings
        DrawRect(x - 16, y + 2, 14, 8, PlayerWing);
        DrawRect(x + 16, y + 2, 14, 8, PlayerWing);
        // Wing tips
        DrawRect(x - 24, y + 4, 4, 5, PlayerBody);
        DrawRect(x + 24, y + 4, 4, 5, PlayerBody);
        // Tail fins
        DrawRect(x - 8, y + 14, 6, 5, PlayerWing);
        DrawRect(x + 8, y + 14, 6, 5, PlayerWing);
        // Engine glow (flickers)
        if (_frame % 4 < 2)
        {
            DrawRect(x - 3, y + 18, 3, 4, PlayerEngine);
            DrawRect(x + 3, y + 18, 3, 4, PlayerEngine);
        }
    }

    private void DrawEnemy(Enemy e)
    {
        double x = e.X, y = e.Y;
        switch (e.Type)
        {
            case EnemyType.Scout:
                // Slim, fast-looking
                DrawRect(x, y, 6, 16, ScoutBody);
                DrawRect(x - 8, y + 2, 8, 5, ScoutAccent);
                DrawRect(x + 8, y + 2, 8, 5, ScoutAccent);
                DrawRect(x, y + 8, 4, 4, ScoutAccent);
                break;

            case EnemyType.Fighter:
                // Medium with swept wings
                DrawRect(x, y, 8, 20, FighterBody);
                DrawRect(x - 12, y + 3, 12, 6, FighterAccent);
                DrawRect(x + 12, y + 3, 12, 6, FighterAccent);
                DrawRect(x, y - 8, 4, 6, FighterBody);
                DrawRect(x - 5, y + 10, 4, 5, FighterAccent);
                DrawRect(x + 5, y + 10, 4, 5, FighterAccent);
                break;

            case EnemyType.Bomber:
                // Wide, heavy body
                DrawRect(x, y, 18, 22, BomberBody);
                DrawRect(x - 14, y, 10, 10, BomberAccent);
                DrawRect(x + 14, y, 10, 10, BomberAccent);
                DrawRect(x, y - 10, 8, 6, BomberBody);
                DrawRect(x - 8, y + 12, 6, 4, BomberAccent);
                DrawRect(x + 8, y + 12, 6, 4, BomberAccent);
                // Bomb bay
                DrawRect(x, y + 10, 10, 4, BomberAccent);
                break;

            case EnemyType.Ace:
                // Sleek, angled, aggressive
                DrawRect(x, y, 8, 18, AceBody);
                DrawRect(x - 10, y + 4, 10, 5, AceAccent);
                DrawRect(x + 10, y + 4, 10, 5, AceAccent);
                DrawRect(x - 14, y + 6, 4, 3, AceBody);
                DrawRect(x + 14, y + 6, 4, 3, AceBody);
                DrawRect(x, y - 8, 4, 6, AceAccent);
                break;

            case EnemyType.Boss:
                DrawBoss(e);
                break;
        }
    }

    private void DrawBoss(Enemy e)
    {
        double x = e.X, y = e.Y;
        double pct = (double)e.Hp / e.MaxHp;

        switch (e.BossId)
        {
            case 0: // Mothership — wide, flat carrier with hangars
                DrawRect(x, y, 60, 28, Boss0Body);
                DrawRect(x, y - 12, 36, 8, Boss0Accent);
                DrawRect(x - 34, y + 2, 12, 20, Boss0Accent);  // left hangar
                DrawRect(x + 34, y + 2, 12, 20, Boss0Accent);  // right hangar
                DrawRect(x - 34, y + 14, 8, 6, Boss0Detail);   // hangar bay
                DrawRect(x + 34, y + 14, 8, 6, Boss0Detail);
                DrawRect(x, y + 16, 20, 6, Boss0Detail);       // center bay
                break;

            case 1: // Fortress — tall armored tower with turrets
                DrawRect(x, y, 40, 44, Boss1Body);
                DrawRect(x, y - 18, 28, 10, Boss1Accent);      // top armor
                DrawRect(x - 28, y - 4, 12, 16, Boss1Detail);  // left turret
                DrawRect(x + 28, y - 4, 12, 16, Boss1Detail);  // right turret
                DrawRect(x - 28, y + 6, 4, 8, Boss1Accent);    // turret barrel L
                DrawRect(x + 28, y + 6, 4, 8, Boss1Accent);    // turret barrel R
                DrawRect(x, y + 20, 24, 6, Boss1Accent);       // base
                DrawRect(x - 10, y, 6, 6, Boss1Detail);        // viewport L
                DrawRect(x + 10, y, 6, 6, Boss1Detail);        // viewport R
                break;

            case 2: // Phantom — sleek, narrow, extremely long wings
                DrawRect(x, y, 12, 24, Boss2Body);
                DrawRect(x, y - 10, 6, 8, Boss2Accent);        // nose
                DrawRect(x - 36, y + 2, 30, 6, Boss2Accent);   // left wing (long!)
                DrawRect(x + 36, y + 2, 30, 6, Boss2Accent);   // right wing
                DrawRect(x - 48, y + 4, 6, 4, Boss2Detail);    // wing tip L
                DrawRect(x + 48, y + 4, 6, 4, Boss2Detail);    // wing tip R
                DrawRect(x, y + 12, 8, 4, Boss2Detail);        // tail
                break;

            case 3: // Bombardier — fat round body, bomb bays
                DrawRect(x, y, 52, 34, Boss3Body);
                DrawRect(x, y - 14, 32, 8, Boss3Accent);       // top
                DrawRect(x - 18, y + 6, 12, 14, Boss3Accent);  // left engine
                DrawRect(x + 18, y + 6, 12, 14, Boss3Accent);  // right engine
                DrawRect(x - 8, y + 16, 6, 6, Boss3Detail);    // bomb bay L
                DrawRect(x, y + 16, 6, 6, Boss3Detail);        // bomb bay C
                DrawRect(x + 8, y + 16, 6, 6, Boss3Detail);    // bomb bay R
                DrawRect(x, y + 4, 20, 8, Boss3Detail);        // belly
                break;

            case 4: // Overlord — massive, crown on top
                DrawRect(x, y, 56, 38, Boss4Body);
                DrawRect(x, y - 16, 36, 10, Boss4Accent);
                // Crown (gold spikes)
                DrawRect(x - 14, y - 24, 4, 10, Boss4Crown);
                DrawRect(x - 7, y - 28, 4, 14, Boss4Crown);
                DrawRect(x, y - 26, 4, 12, Boss4Crown);
                DrawRect(x + 7, y - 28, 4, 14, Boss4Crown);
                DrawRect(x + 14, y - 24, 4, 10, Boss4Crown);
                // Wings
                DrawRect(x - 34, y + 4, 14, 18, Boss4Accent);
                DrawRect(x + 34, y + 4, 14, 18, Boss4Accent);
                DrawRect(x, y + 20, 16, 6, Boss4Accent);       // tail
                // Eyes
                DrawRect(x - 10, y - 4, 6, 6, Boss4Crown);
                DrawRect(x + 10, y - 4, 6, 6, Boss4Crown);
                break;
        }

        // HP bar (all bosses)
        DrawRect(x, y - 34, 64 * pct, 4, pct > 0.5 ? SpreadColor : FighterBody);
        DrawRect(x, y - 34, 64, 4, new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)));
    }

    static (IBrush brush, string label) PuVisual(PowerUpType t) => t switch
    {
        PowerUpType.Spread => (SpreadColor, "S"),
        PowerUpType.Shield => (ShieldColor, "P"),
        PowerUpType.Rapid => (RapidColor, "R"),
        PowerUpType.Bomb => (BombColor, "B"),
        PowerUpType.Magnet => (MagnetColor, "M"),
        PowerUpType.Life => (LifeColor, "\u2764"),
        _ => (DimBrush, "?")
    };

    // ── Drawing helpers ──

    private void DrawRect(double cx, double cy, double w, double h, IBrush fill)
    {
        var r = new Rectangle { Width = w, Height = h, Fill = fill };
        Canvas.SetLeft(r, cx - w / 2); Canvas.SetTop(r, cy - h / 2);
        gameCanvas.Children.Add(r);
    }

    private void DrawText(string text, double x, double y, double size, IBrush brush,
                           bool centerX, bool rightAlign = false)
    {
        var tb = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeight.Bold,
            Foreground = brush, FontFamily = new FontFamily("monospace") };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = tb.DesiredSize.Width;
        Canvas.SetLeft(tb, centerX ? x - tw / 2 : rightAlign ? x - tw : x);
        Canvas.SetTop(tb, y);
        gameCanvas.Children.Add(tb);
    }
}
