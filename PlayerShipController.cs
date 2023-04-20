using Mirror;
using Spine;
using Spine.Unity;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using Utils.Joysticks;
using static PlayerShipStatsController;
using static SeaController;
#pragma warning disable 0649

public class PlayerShipController : NetworkBehaviour
{
    [SerializeField] private Joystick directionJoystick;
    [SerializeField] private string sinkAnimation;
    [SerializeField] private string hitAnimation;
    [SerializeField] private string shootAnimation;
    [SerializeField] private float torque;
    [SerializeField] private float slowDistance;
    [SerializeField] private float shotSpeed;
    [SerializeField] private float shotLoadingTime = 0.5f;
    [SerializeField] private SkeletonAnimation statusBarAnimator;
    [SerializeField] private string statusBarAnimation;
    [SerializeField] private Vector2 cannonOffset;
    [SerializeField] private GameObject trial;
    [SerializeField] private TextMeshProUGUI playerNameText;
    [SerializeField] private GameObject cannonBallPrefab;

    internal float shootingRadius = 3;
    internal List<Transform> enemiesInRaduis;
    internal Vector3 previousPosition;
    internal Vector3 direction;
    internal Vector3 destination;

    private Vector3 initialPosition;
    private SeaController seaController;
    private PlayerShipStatsController statsController;
    private ShipLifeController lifeController;
    private SkeletonAnimation animator;
    private Rigidbody2D boatRigidbody;
    private PoolController cannonBallPool;
    private MeshRenderer meshRenderer;
    private GUIStyle style;
    private bool readyToMove = false;
    private bool isDying = false;
    private TrackEntry statusBarTrackEntry;
    private float cannonBarMaxSize;
    private float statusBarStep;
    private bool shootTutorialShown = false;
    [SyncVar] internal string scoreTable;
    [SyncVar(hook = nameof(UpdateNameUI))] internal string playerName;
    [SyncVar] internal int score = 0;
    [SyncVar] private float speed = 10;
    [SyncVar] private float damage = 25f;
    [SyncVar] internal float timeToShoot = 0;

    private void Awake()
    {
        cannonBallPool = GameObject.Find(Constants.GAMEOBJECT_SHIP_CANNON_BALL_POOL).GetComponent<PoolController>();
        animator = GetComponent<SkeletonAnimation>();
        lifeController = GetComponent<ShipLifeController>();
        meshRenderer = GetComponent<MeshRenderer>();
        enemiesInRaduis = new List<Transform>();
        statsController = GetComponent<PlayerShipStatsController>();
        seaController = GameObject.Find(Constants.GAMEOBJECT_GAME_MASTER).GetComponent<SeaController>();
        GameObject _joystickGameObject = GameObject.Find(Constants.GAMEOBJECT_JOYSTICK);
        if (_joystickGameObject != null)
            directionJoystick = _joystickGameObject.GetComponent<SeaJoystick>();
    }


    private void Start()
    {
        isDying = false;
        initialPosition = previousPosition = transform.position;
        boatRigidbody = GetComponent<Rigidbody2D>();
        boatRigidbody.freezeRotation = true;
        trial.SetActive(true);
        if (isClient)
            animator.state.SetAnimation(2, hitAnimation, false);
        if (isLocalPlayer)
        {
            CameraBoatFollower cameraBoatFollower = Camera.main.GetComponent<CameraBoatFollower>();
            cameraBoatFollower.boat = transform;
            seaController.shipController = this;
            StartCoroutine(seaController.InitAutomaticQuestsCoroutine());
            CmdInitLocalPlayerInServer();
            MultiShipsManager.instance.localPlayer = this;
            CmdSetPlayerName(PlayerPrefs.GetString(Constants.PLAYERPREFS_PLAYER_NAME));
        }
        SetUpStatusBar();
        UpdateNameUI("", playerName);
    }

    internal void Init()
    {
        if (isLocalPlayer)
            CmdSetPlayerName(PlayerPrefs.GetString(Constants.PLAYERPREFS_PLAYER_NAME));
        GameObject _joystickGameObject = GameObject.Find(Constants.GAMEOBJECT_JOYSTICK);
        if (_joystickGameObject != null)
            directionJoystick = _joystickGameObject.GetComponent<SeaJoystick>();
    }
    private void OnEnable()
    {
        lifeController.OnDeath += OnDeath;
        lifeController.OnHit += PlayHitAnimation;
    }

    private void OnDisable()
    {
        lifeController.OnDeath -= OnDeath;
        lifeController.OnHit -= PlayHitAnimation;
        if (isServer)
            ScoreTableController.instance.RefreshScoreTable();
    }



    void Update()
    {
        if (isDying || isGamePaused) return;
        if (isServer)
            ProcessCanonLoading();
        if (isLocalPlayer)
        {
            ProcessInputJoystick();
            UpdateSortingOrder();
            SetSailingAnimation();
        }
    }

    private void FixedUpdate()
    {
        if (isDying) return;
        ProcessInputPhysics();
    }
    private void UpdateSortingOrder()
    {
        meshRenderer.sortingOrder = (int)(transform.position.y * 10) * -1;
    }
    private void ProcessInputJoystick()
    {
        if (directionJoystick != null)
        {
            SetShipDirection(directionJoystick.Horizontal);
            if (directionJoystick.Direction != Vector2.zero)
            {
                direction = directionJoystick.Direction;
                readyToMove = true;
            }
            else
            {
                CmdTryToShoot();
            }
        }
    }
    private void SetSailingAnimation()
    {
        if (animator.state.GetCurrent(0) != null)
        {
            if (directionJoystick != null)
            {
                if (directionJoystick.Direction == Vector2.zero && animator.state.GetCurrent(0).Animation.Name != statsController.GetAnimation(ShipState.Idle))
                    CmdSetSailingAnimation(false);
                if (directionJoystick.Direction != Vector2.zero && animator.state.GetCurrent(0).Animation.Name != statsController.GetAnimation(ShipState.Saling))
                    CmdSetSailingAnimation(true);
            }
        }
    }

    private void SetShipDirection(float horizontal)
    {
        float _previousScale = animator.skeleton.ScaleX;
        if (horizontal < 0 && animator.skeleton.ScaleX > 0)
            animator.skeleton.ScaleX = animator.skeleton.ScaleX * -1;
        if (horizontal > 0 && animator.skeleton.ScaleX < 0)
            animator.skeleton.ScaleX = animator.skeleton.ScaleX * -1;
        if (_previousScale != animator.skeleton.ScaleX)
            CmdSetShipDirection(animator.skeleton.ScaleX);
    }
    private void ProcessInputPhysics()
    {
        if (readyToMove)
        {
            MoveShip(direction, speed, torque);
            readyToMove = false;
        }
    }

    public void MoveShip(Vector2 direction, float speed, float torque)
    {
        if (direction != Vector2.zero)
        {
            boatRigidbody.AddForce(direction * torque);
            if (boatRigidbody.velocity.magnitude > speed)
                boatRigidbody.velocity = boatRigidbody.velocity.normalized * speed;
        }
    }

    private void SetUpStatusBar()
    {
        statusBarTrackEntry = statusBarAnimator.state.SetAnimation(1, statusBarAnimation, false);
        statusBarTrackEntry.MixDuration = 0;
        statusBarTrackEntry.TimeScale = 0;
        cannonBarMaxSize = statusBarTrackEntry.AnimationEnd * Constants.FRAME_RATE;
        statusBarStep = cannonBarMaxSize / shotLoadingTime;
        UpdateStatusBar();
    }
    private void UpdateStatusBar()
    {
        if (statusBarTrackEntry != null)
        {
            statusBarTrackEntry.TrackTime = ((shotLoadingTime - timeToShoot) * statusBarStep) / Constants.FRAME_RATE;
            statusBarTrackEntry.TimeScale = 0;
        }
    }

    internal void PlayShotAnimation()
    {
        if (isDying) return;
        TrackEntry trackEntry = animator.state.SetAnimation(3, shootAnimation, false);
        trackEntry.MixDuration = 0;
        SoundManager.instance.PlayCannonShot();
    }

    internal void DestroyCannonBall(GameObject _gameObject)
    {
        CmdDestroyCannonBall(_gameObject);
    }

    [ClientRpc]
    internal void RpcPlayHitAnimation()
    {
        //TrackEntry trackEntry = animator.state.SetAnimation(2, sinkAnimation, false);
        TrackEntry trackEntry = animator.state.SetAnimation(2, hitAnimation, false);
        trackEntry.MixDuration = 0;
        SoundManager.instance.PlayCannonHit();
    }

    [ClientRpc]
    private void RpcUpdateStatusBar()
    {
        UpdateStatusBar();
    }

    [ClientRpc]
    private void RpcPlayShotAnimation(Vector2 _destination, float _speed, ShipCannonBallController _cannonBall)
    {
        _cannonBall.ClientInit(transform.position, _destination, _speed);
        PlayShotAnimation();
    }
    [ClientRpc]
    private void RpcSetSailingAnimation(bool _isSailing)
    {
        if (_isSailing)
            animator.state.SetAnimation(0, statsController.GetAnimation(ShipState.Saling), true);
        else
            animator.state.SetAnimation(0, statsController.GetAnimation(ShipState.Idle), true);
    }
    [ClientRpc]
    private void RpcSetShipDirection(float _scale)
    {
        animator.skeleton.ScaleX = _scale;
    }

    [ClientRpc]
    private void RpcShowShootingTutorial()
    {
        if (!shootTutorialShown)
        {
            shootTutorialShown = true;
        }
    }

    [Command]
    private void CmdInitLocalPlayerInServer()
    {
        MultiShipsManager.instance.Add(this);
        SetStats();
        UpdatePlayersInRadius();
    }
    [Server]
    internal void SetStats()
    {
        lifeController.SetMaxLife(statsController.GetLife());
        speed = statsController.GetSpeed();
        damage = statsController.GetDamage();
    }

    [Server]
    private void ProcessCanonLoading()
    {
        //UpdatePlayersInRadius();
        if (timeToShoot > 0)
        {
            timeToShoot -= Time.deltaTime;
            RpcUpdateStatusBar();
        }
    }

    [Command]
    private void CmdSetSailingAnimation(bool _isSailing)
    {
        RpcSetSailingAnimation(_isSailing);
    }

    [Command]
    private void CmdTryToShoot()
    {
        UpdatePlayersInRadius();
        if (enemiesInRaduis.Count > 0)
        {
            //RpcShowShootingTutorial();
            if (timeToShoot <= 0)
            {
                timeToShoot = shotLoadingTime;
                RpcUpdateStatusBar();
                enemiesInRaduis = enemiesInRaduis.OrderBy(x => Vector2.Distance(transform.position, x.position)).ToList();
                Transform _closestEnemy = enemiesInRaduis[0];
                if (_closestEnemy != null)
                    Shoot(_closestEnemy.transform.position);
            }
        }
    }

    [Server]
    internal void PlayHitAnimation()
    {
        if (isDying) return;
        RpcPlayHitAnimation();
    }

    [Server]
    private void UpdatePlayersInRadius()
    {
        if (MultiShipsManager.instance != null)
        {
            List<PlayerShipController> _playersToRemove = new List<PlayerShipController>();
            enemiesInRaduis.Clear();
            foreach (PlayerShipController _otherPlayer in MultiShipsManager.instance.playerShips)
            {
                if (_otherPlayer == null)
                {
                    _playersToRemove.Add(_otherPlayer);
                }
                else
                {
                    if (_otherPlayer != this)
                    {
                        float _distanceToPlayer = Vector2.Distance(_otherPlayer.transform.position, transform.position);
                        if (_distanceToPlayer < shootingRadius)
                        {
                            if (!enemiesInRaduis.Contains(_otherPlayer.transform))
                                enemiesInRaduis.Add(_otherPlayer.transform);
                        }
                        else
                        {
                            if (enemiesInRaduis.Contains(_otherPlayer.transform))
                                enemiesInRaduis.Remove(_otherPlayer.transform);
                        }
                    }
                }
            }
            foreach (PlayerShipController _playerToRemove in _playersToRemove)
                MultiShipsManager.instance.playerShips.Remove(_playerToRemove);
        }
    }

    [Command]
    private void CmdSetPlayerName(string _name)
    {
        playerName = _name;
        ScoreTableController.instance.RefreshScoreTable();
    }
    [Command]
    private void CmdSetShipDirection(float _scale)
    {
        RpcSetShipDirection(_scale);
    }

    [Server]
    private void Shoot(Vector2 _shootDestination)
    {
        ShipCannonBallController _cannonBall = Instantiate(cannonBallPrefab).GetComponent<ShipCannonBallController>();
        _cannonBall.transform.position = transform.position + (Vector3)cannonOffset;
        _cannonBall.Init(_shootDestination, shotSpeed, damage, gameObject.GetInstanceID(), this);
        NetworkServer.Spawn(_cannonBall.gameObject);
        RpcPlayShotAnimation(_shootDestination, shotSpeed, _cannonBall);
    }

    [ClientRpc]
    private void RpcOnDeath()
    {
        Debug.Log("Starting dead coroutine on client");
        TrackEntry _trackEntry = animator.state.SetAnimation(2, sinkAnimation, false);
        _trackEntry.MixDuration = 0;
        StartCoroutine(DeathCoroutine());
    }

    [Client]
    private IEnumerator DeathCoroutine()
    {
        trial.SetActive(false);
        lifeController.Hide();
        while (!animator.state.GetCurrent(2).IsComplete)
            yield return null;
        GetComponent<MeshRenderer>().enabled = false;
        if (isLocalPlayer)
            CmdStartRespawn();
    }

    [Server]
    public void OnDeath()
    {
        ScoreTableController.instance.RefreshScoreTable();
        Debug.Log(playerName + " died.");
        isDying = true;
        MultiShipsManager.instance.Remove(this);
        RpcOnDeath();
    }

    [Command]
    internal void CmdDestroyCannonBall(GameObject _gameObject)
    {
        NetworkServer.Destroy(_gameObject);
    }

    [Command]
    private void CmdStartRespawn()
    {
        MultiShipsManager.instance.Remove(this);
        trial.SetActive(false);
        RpcRespawn();
    }

    [Command]
    private void CmdCompleteRespawn()
    {

        lifeController.Heal();
        isDying = false;
        initialPosition = previousPosition = transform.position;
        MultiShipsManager.instance.Add(this);
        SetStats();
        UpdatePlayersInRadius();
        UpdateStatusBar();
        ScoreTableController.instance.RefreshScoreTable();
    }

    [ClientRpc]
    private void RpcRespawn()
    {
        transform.position = MirrorNetworkManager.instance.spawnPoints[UnityEngine.Random.Range(0, MirrorNetworkManager.instance.spawnPoints.Count)];
        isDying = false;
        initialPosition = previousPosition = transform.position;
        animator.state.ClearTracks();
        animator.skeleton.SetToSetupPose();
        animator.state.SetAnimation(0, statsController.GetAnimation(ShipState.Idle), true);
        StartCoroutine(ActivateChildrenCoroutine());
    }

    [Client]
    IEnumerator ActivateChildrenCoroutine()
    {
        yield return new WaitForSeconds(1f);
        trial.SetActive(true);
        lifeController.Show();
        GetComponent<MeshRenderer>().enabled = true;
        if (isLocalPlayer)
        {
            CmdCompleteRespawn();
        }
    }

    private void UpdateNameUI(string _oldName, string _newName)
    {
        playerNameText.text = _newName;
    }
}
