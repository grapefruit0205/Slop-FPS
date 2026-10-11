import test from 'node:test';
import assert from 'node:assert/strict';
import { createApp, autoRoomName, cleanText, MODES, MAPS, DEFAULT_MODE, DEFAULT_MAP } from '../src/app.mjs';

const ROOM_ID = 'arn:aws:gamelift:ap-northeast-2::gamesession/fleet-abc/gsess-1';
const ENV = { FLEET_ALIAS_ID: 'alias-123', MAX_PLAYERS: '8', ALLOWED_ORIGINS: 'https://game.example.com' };

const gs = (over = {}) => ({
  GameSessionId: ROOM_ID, Name: 'Alpha', Status: 'ACTIVE',
  CurrentPlayerSessionCount: 3, MaximumPlayerSessionCount: 8,
  DnsName: 'ec2-1.compute.amazonaws.com', IpAddress: '1.2.3.4', Port: 7771, ...over,
});
const props = (mode, map) => [{ Key: 'mode', Value: mode }, { Key: 'map', Value: map }];
const gsId = (n) => `arn:aws:gamelift:x::gamesession/f/${n}`;
/** CreateGameSession handler that echoes the requested GameProperties back, like GameLift does. */
const echoCreate = (over = {}) => (input) => ({
  GameSession: gs({ Name: input.Name, GameProperties: input.GameProperties, CurrentPlayerSessionCount: 0, ...over }),
});
const playerSession = (over = {}) => ({
  PlayerSession: { PlayerSessionId: 'psess-abc', DnsName: 'dns.example.com', IpAddress: '1.2.3.4', Port: 7771, ...over },
});
const awsError = (name) => Object.assign(new Error(name), { name });

/** Fake client: `handlers` maps command class name -> (input, callIndex) => output | throws. */
function fakeClient(handlers) {
  const calls = [];
  return {
    calls,
    async send(cmd) {
      const name = cmd.constructor.name.replace(/Command$/, '');
      const n = calls.filter((c) => c.name === name).length;
      calls.push({ name, input: cmd.input });
      if (!handlers[name]) throw new Error(`unexpected command ${name}`);
      return handlers[name](cmd.input, n);
    },
  };
}

function makeApp(handlers, env = ENV) {
  let t = 0; // fake clock: sleep advances time
  const client = fakeClient(handlers);
  const handle = createApp({ client, env, now: () => t, sleep: async (ms) => { t += ms; }, rng: () => 0.5 });
  const call = async (method, path, body, headers = {}) => {
    const res = await handle({
      requestContext: { http: { method } }, rawPath: path, headers,
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    return { status: res.statusCode, json: res.body ? JSON.parse(res.body) : null, headers: res.headers };
  };
  return { client, call };
}

test('GET /rooms lists rooms sorted by players desc, includes full rooms, drops terminated', async () => {
  const { call, client } = makeApp({
    SearchGameSessions: () => ({
      GameSessions: [
        gs({ GameSessionId: gsId('a'), Name: 'A', CurrentPlayerSessionCount: 1 }), // no GameProperties -> defaults
        gs({ GameSessionId: gsId('b'), Name: 'B', CurrentPlayerSessionCount: 8, GameProperties: props('ffa', 'outpost') }),
        gs({ GameSessionId: gsId('c'), Name: 'C', Status: 'TERMINATING' }),
      ],
    }),
  });
  const r = await call('GET', '/rooms');
  assert.equal(r.status, 200);
  assert.deepEqual(r.json, { rooms: [
    { id: gsId('b'), name: 'B', mode: 'ffa', map: 'outpost', players: 8, maxPlayers: 8, status: 'ACTIVE' },
    { id: gsId('a'), name: 'A', mode: 'tdm', map: 'warehouse', players: 1, maxPlayers: 8, status: 'ACTIVE' },
  ] });
  const input = client.calls[0].input;
  assert.equal(input.AliasId, 'alias-123');
  assert.equal(input.SortExpression, 'playerSessionCount DESC');
  assert.equal(input.FilterExpression, undefined); // full rooms must be listed
});

test('POST /rooms creates, waits for ACTIVE, creates player session', async () => {
  const { call, client } = makeApp({
    CreateGameSession: () => ({ GameSession: gs({ Status: 'ACTIVATING', CurrentPlayerSessionCount: 0 }) }),
    DescribeGameSessions: (_i, n) => ({ GameSessions: [gs({ Status: n < 2 ? 'ACTIVATING' : 'ACTIVE', CurrentPlayerSessionCount: 0 })] }),
    CreatePlayerSession: () => playerSession(),
  });
  const r = await call('POST', '/rooms', { name: 'My <b>Room</b>', playerName: 'Kim' });
  assert.equal(r.status, 200);
  assert.deepEqual(r.json.room, { id: ROOM_ID, name: 'Alpha', mode: 'tdm', map: 'warehouse', players: 1, maxPlayers: 8, status: 'ACTIVE' });
  assert.deepEqual(r.json.connect, { host: 'dns.example.com', port: 7771, playerSessionId: 'psess-abc' });
  const create = client.calls.find((c) => c.name === 'CreateGameSession').input;
  assert.equal(create.Name, 'My bRoom/b'); // < and > stripped
  assert.equal(create.MaximumPlayerSessionCount, 8);
  assert.deepEqual(create.GameProperties, props('tdm', 'warehouse')); // omitted mode/map -> defaults
  assert.equal(create.AliasId, 'alias-123');
  assert.equal(client.calls.filter((c) => c.name === 'DescribeGameSessions').length, 3);
  // GameLift rejects DescribeGameSessions with more than one of GameSessionId / AliasId / FleetId.
  for (const c of client.calls.filter((c) => c.name === 'DescribeGameSessions')) {
    assert.deepEqual(Object.keys(c.input), ['GameSessionId']);
  }
  const ps = client.calls.find((c) => c.name === 'CreatePlayerSession').input;
  assert.equal(ps.PlayerData, 'Kim');
  assert.ok(ps.PlayerId);
});

test('POST /rooms with empty name uses auto name', async () => {
  const { call, client } = makeApp({
    CreateGameSession: () => ({ GameSession: gs() }),
    CreatePlayerSession: () => playerSession(),
  });
  const r = await call('POST', '/rooms', { name: '', playerName: 'Kim' });
  assert.equal(r.status, 200);
  assert.equal(client.calls[0].input.Name, 'Kim의 작전 #550'); // rng 0.5 -> 100 + 450
});

test('POST /rooms returns SERVER_STARTING when session never becomes ACTIVE', async () => {
  const { call } = makeApp({
    CreateGameSession: () => ({ GameSession: gs({ Status: 'ACTIVATING' }) }),
    DescribeGameSessions: () => ({ GameSessions: [gs({ Status: 'ACTIVATING' })] }),
  });
  const r = await call('POST', '/rooms', { playerName: 'Kim' });
  assert.equal(r.status, 503);
  assert.equal(r.json.error, 'SERVER_STARTING');
});

test('POST /rooms/join joins an existing room', async () => {
  const { call, client } = makeApp({
    DescribeGameSessions: () => ({ GameSessions: [gs()] }),
    CreatePlayerSession: () => playerSession(),
  });
  const r = await call('POST', '/rooms/join', { roomId: ROOM_ID, playerName: 'Lee' });
  assert.equal(r.status, 200);
  assert.equal(r.json.room.players, 4);
  assert.equal(r.json.connect.playerSessionId, 'psess-abc');
  assert.equal(client.calls[1].input.GameSessionId, ROOM_ID);
});

test('POST /rooms/join unknown room -> 404', async () => {
  const { call } = makeApp({ DescribeGameSessions: () => ({ GameSessions: [] }) });
  const r = await call('POST', '/rooms/join', { roomId: ROOM_ID, playerName: 'Lee' });
  assert.equal(r.status, 404);
  assert.equal(r.json.error, 'NOT_FOUND');
});

test('ROOM_FULL: GameSessionFullException and pre-checked full room both -> 409', async () => {
  let app = makeApp({
    DescribeGameSessions: () => ({ GameSessions: [gs()] }),
    CreatePlayerSession: () => { throw awsError('GameSessionFullException'); },
  });
  let r = await app.call('POST', '/rooms/join', { roomId: ROOM_ID, playerName: 'Lee' });
  assert.equal(r.status, 409);
  assert.equal(r.json.error, 'ROOM_FULL');

  app = makeApp({
    DescribeGameSessions: () => ({ GameSessions: [gs({ CurrentPlayerSessionCount: 8 })] }),
  });
  r = await app.call('POST', '/rooms/join', { roomId: ROOM_ID, playerName: 'Lee' });
  assert.equal(r.status, 409);
  assert.equal(r.json.error, 'ROOM_FULL');

  app = makeApp({
    DescribeGameSessions: () => ({ GameSessions: [gs()] }),
    CreatePlayerSession: () => { throw awsError('InvalidGameSessionStatusException'); },
  });
  r = await app.call('POST', '/rooms/join', { roomId: ROOM_ID, playerName: 'Lee' });
  assert.equal(r.json.error, 'ROOM_FULL');
});

test('FLEET_FULL: FleetCapacityExceededException on create -> 503', async () => {
  const { call } = makeApp({
    CreateGameSession: () => { throw awsError('FleetCapacityExceededException'); },
  });
  const r = await call('POST', '/rooms', { playerName: 'Kim' });
  assert.equal(r.status, 503);
  assert.equal(r.json.error, 'FLEET_FULL');
});

test('POST /quickjoin joins the fullest open room', async () => {
  const { call, client } = makeApp({
    SearchGameSessions: () => ({ GameSessions: [gs({ CurrentPlayerSessionCount: 6 }), gs({ GameSessionId: 'arn:aws:gamelift:x::gamesession/f/z', CurrentPlayerSessionCount: 2 })] }),
    CreatePlayerSession: () => playerSession(),
  });
  const r = await call('POST', '/quickjoin', { playerName: 'Park' });
  assert.equal(r.status, 200);
  assert.equal(r.json.room.id, ROOM_ID);
  assert.equal(r.json.room.players, 7);
  assert.equal(client.calls[0].input.FilterExpression, 'hasAvailablePlayerSessions=true');
  assert.equal(client.calls[0].input.SortExpression, 'playerSessionCount DESC');
  assert.equal(client.calls.some((c) => c.name === 'CreateGameSession'), false);
});

test('POST /quickjoin falls through to the next candidate when the first is full', async () => {
  const second = 'arn:aws:gamelift:x::gamesession/f/second';
  const { call } = makeApp({
    SearchGameSessions: () => ({ GameSessions: [gs({ CurrentPlayerSessionCount: 7 }), gs({ GameSessionId: second, CurrentPlayerSessionCount: 2 })] }),
    CreatePlayerSession: (i) => { if (i.GameSessionId === ROOM_ID) throw awsError('GameSessionFullException'); return playerSession(); },
  });
  const r = await call('POST', '/quickjoin', { playerName: 'Park' });
  assert.equal(r.status, 200);
  assert.equal(r.json.room.id, second);
});

test('POST /quickjoin creates a room when none is available', async () => {
  const { call, client } = makeApp({
    SearchGameSessions: () => ({ GameSessions: [] }),
    CreateGameSession: () => ({ GameSession: gs({ CurrentPlayerSessionCount: 0 }) }),
    CreatePlayerSession: () => playerSession({ DnsName: undefined }),
  });
  const r = await call('POST', '/quickjoin', { playerName: 'Park' });
  assert.equal(r.status, 200);
  assert.equal(r.json.room.players, 1);
  assert.equal(r.json.connect.host, 'ec2-1.compute.amazonaws.com'); // falls back to game session DNS
  assert.match(client.calls.find((c) => c.name === 'CreateGameSession').input.Name, /^Park의 작전 #\d{3}$/);
});

test('connect falls back to IpAddress when no DnsName anywhere', async () => {
  const { call } = makeApp({
    SearchGameSessions: () => ({ GameSessions: [] }),
    CreateGameSession: () => ({ GameSession: gs({ DnsName: undefined }) }),
    CreatePlayerSession: () => playerSession({ DnsName: undefined }),
  });
  const r = await call('POST', '/quickjoin', { playerName: 'Park' });
  assert.equal(r.json.connect.host, '1.2.3.4');
});

test('validation errors -> 400 BAD_REQUEST', async () => {
  const { call, client } = makeApp({});
  const cases = [
    ['POST', '/rooms', { playerName: '' }],
    ['POST', '/rooms', {}],
    ['POST', '/rooms', { playerName: 'x'.repeat(17) }],
    ['POST', '/rooms', { playerName: '<>\u0007' }], // empty after stripping
    ['POST', '/rooms', { playerName: 'ok', name: 'y'.repeat(25) }],
    ['POST', '/rooms', { playerName: 42 }],
    ['POST', '/quickjoin', { playerName: '' }],
    ['POST', '/rooms/join', { playerName: 'ok' }],
    ['POST', '/rooms/join', { roomId: 'gsess-123', playerName: 'ok' }],
    ['POST', '/rooms/join', { roomId: 'arn:aws:gamelift:' + 'a'.repeat(300), playerName: 'ok' }],
    ['POST', '/rooms/join', { roomId: 'arn:aws:gamelift:x y', playerName: 'ok' }],
  ];
  for (const [m, p, b] of cases) {
    const r = await call(m, p, b);
    assert.equal(r.status, 400, JSON.stringify(b));
    assert.equal(r.json.error, 'BAD_REQUEST');
  }
  assert.equal(client.calls.length, 0, 'no AWS call on invalid input');
  // malformed JSON
  const res = await createApp({ client: client, env: ENV })({ requestContext: { http: { method: 'POST' } }, rawPath: '/rooms', body: '{nope' });
  assert.equal(res.statusCode, 400);
});

// ---- mode / map ----------------------------------------------------------------
test('mode/map lists and defaults', () => {
  assert.deepEqual(MODES, ['tdm', 'ffa', 'elim']);
  assert.deepEqual(MAPS, ['warehouse', 'outpost']);
  assert.ok(MODES.includes(DEFAULT_MODE) && MAPS.includes(DEFAULT_MAP));
  assert.equal(DEFAULT_MODE, 'tdm');
  assert.equal(DEFAULT_MAP, 'warehouse');
});

test('POST /rooms passes mode and map to CreateGameSession and returns them', async () => {
  const { call, client } = makeApp({
    CreateGameSession: echoCreate(),
    CreatePlayerSession: () => playerSession(),
  });
  const r = await call('POST', '/rooms', { name: 'FFA fun', playerName: 'Kim', mode: 'ffa', map: 'outpost' });
  assert.equal(r.status, 200);
  assert.equal(r.json.room.mode, 'ffa');
  assert.equal(r.json.room.map, 'outpost');
  assert.deepEqual(r.json.connect, { host: 'dns.example.com', port: 7771, playerSessionId: 'psess-abc' });
  const create = client.calls.find((c) => c.name === 'CreateGameSession').input;
  assert.deepEqual(create.GameProperties, props('ffa', 'outpost'));
  assert.equal(create.MaximumPlayerSessionCount, 8);
});

test('POST /rooms creates an elimination room (mode elim) and lists it back', async () => {
  const { client, call } = makeApp({
    CreateGameSession: echoCreate(),
    CreatePlayerSession: () => playerSession(),
    SearchGameSessions: () => ({ GameSessions: [gs({ GameProperties: props('elim', 'warehouse') })] }),
  });
  const r = await call('POST', '/rooms', { playerName: 'Kim', mode: 'elim' });
  assert.equal(r.status, 200);
  assert.deepEqual(client.calls[0].input.GameProperties, props('elim', 'warehouse'));
  assert.equal(r.json.room.mode, 'elim');
  const list = await call('GET', '/rooms');
  assert.equal(list.json.rooms[0].mode, 'elim');
});

test('POST /rooms: every listed mode x map is accepted, 8 players max for all', async () => {
  for (const mode of MODES) {
    for (const map of MAPS) {
      const { call, client } = makeApp({ CreateGameSession: echoCreate(), CreatePlayerSession: () => playerSession() });
      const r = await call('POST', '/rooms', { playerName: 'Kim', mode, map });
      assert.equal(r.status, 200, `${mode}/${map}`);
      assert.deepEqual(client.calls[0].input.GameProperties, props(mode, map));
      assert.equal(client.calls[0].input.MaximumPlayerSessionCount, 8);
      assert.deepEqual([r.json.room.mode, r.json.room.map], [mode, map]);
    }
  }
});

test('POST /rooms with only one of mode/map defaults the other; null counts as omitted', async () => {
  let app = makeApp({ CreateGameSession: echoCreate(), CreatePlayerSession: () => playerSession() });
  let r = await app.call('POST', '/rooms', { playerName: 'Kim', mode: 'ffa' });
  assert.equal(r.status, 200);
  assert.deepEqual(app.client.calls[0].input.GameProperties, props('ffa', 'warehouse'));

  app = makeApp({ CreateGameSession: echoCreate(), CreatePlayerSession: () => playerSession() });
  r = await app.call('POST', '/rooms', { playerName: 'Kim', mode: null, map: 'outpost' });
  assert.equal(r.status, 200);
  assert.deepEqual(app.client.calls[0].input.GameProperties, props('tdm', 'outpost'));
});

test('invalid mode/map -> 400 BAD_REQUEST with a Korean message, no AWS call', async () => {
  const { call, client } = makeApp({});
  const cases = [
    ['/rooms', { playerName: 'Kim', mode: 'deathmatch' }, /^mode 값이 올바르지 않습니다. 사용 가능: tdm, ffa, elim$/],
    ['/rooms', { playerName: 'Kim', mode: 'TDM' }, /^mode /], // ids are exact, lower-case
    ['/rooms', { playerName: 'Kim', mode: '' }, /^mode /],
    ['/rooms', { playerName: 'Kim', mode: 1 }, /^mode /],
    ['/rooms', { playerName: 'Kim', mode: ['tdm'] }, /^mode /],
    ['/rooms', { playerName: 'Kim', map: 'moon' }, /^map 값이 올바르지 않습니다. 사용 가능: warehouse, outpost$/],
    ['/rooms', { playerName: 'Kim', map: 'Warehouse' }, /^map /],
    ['/rooms', { playerName: 'Kim', map: false }, /^map /],
    ['/rooms', { playerName: 'Kim', mode: 'ffa', map: 'moon' }, /^map /],
    ['/quickjoin', { playerName: 'Kim', mode: 'solo' }, /^mode /],
    ['/quickjoin', { playerName: 'Kim', map: 'moon' }, /^map /],
  ];
  for (const [path, body, message] of cases) {
    const r = await call('POST', path, body);
    assert.equal(r.status, 400, JSON.stringify(body));
    assert.equal(r.json.error, 'BAD_REQUEST');
    assert.match(r.json.message, message);
  }
  assert.equal(client.calls.length, 0);
});

test('GET /rooms reads mode/map from GameProperties; missing, unknown and legacy values are normalised', async () => {
  const { call } = makeApp({
    SearchGameSessions: () => ({
      GameSessions: [
        gs({ GameSessionId: gsId('a'), CurrentPlayerSessionCount: 5, GameProperties: props('ffa', 'warehouse') }),
        gs({ GameSessionId: gsId('b'), CurrentPlayerSessionCount: 4, GameProperties: [{ Key: 'map', Value: 'Warehouse' }] }), // pre-mode rooms
        gs({ GameSessionId: gsId('c'), CurrentPlayerSessionCount: 3, GameProperties: props('zzz', 'moon') }),
        gs({ GameSessionId: gsId('d'), CurrentPlayerSessionCount: 2, GameProperties: [] }),
        gs({ GameSessionId: gsId('e'), CurrentPlayerSessionCount: 1, GameProperties: [{ Key: 'other', Value: 'x' }, { Key: 'map', Value: 'outpost' }] }),
      ],
    }),
  });
  const r = await call('GET', '/rooms');
  assert.deepEqual(r.json.rooms.map((x) => [x.mode, x.map]), [
    ['ffa', 'warehouse'],
    ['tdm', 'warehouse'],
    ['tdm', 'warehouse'],
    ['tdm', 'warehouse'],
    ['tdm', 'outpost'],
  ]);
});

test('POST /rooms/join returns the joined room mode and map', async () => {
  const { call } = makeApp({
    DescribeGameSessions: () => ({ GameSessions: [gs({ GameProperties: props('ffa', 'outpost') })] }),
    CreatePlayerSession: () => playerSession(),
  });
  const r = await call('POST', '/rooms/join', { roomId: ROOM_ID, playerName: 'Lee' });
  assert.equal(r.status, 200);
  assert.deepEqual(r.json.room, { id: ROOM_ID, name: 'Alpha', mode: 'ffa', map: 'outpost', players: 4, maxPlayers: 8, status: 'ACTIVE' });
});

test('POST /quickjoin prefers a matching mode over a fuller room', async () => {
  const { call, client } = makeApp({
    SearchGameSessions: () => ({ GameSessions: [
      gs({ GameSessionId: gsId('tdm6'), CurrentPlayerSessionCount: 6, GameProperties: props('tdm', 'warehouse') }),
      gs({ GameSessionId: gsId('ffa2'), CurrentPlayerSessionCount: 2, GameProperties: props('ffa', 'warehouse') }),
    ] }),
    CreatePlayerSession: () => playerSession(),
  });
  const r = await call('POST', '/quickjoin', { playerName: 'Park', mode: 'ffa' });
  assert.equal(r.status, 200);
  assert.equal(r.json.room.id, gsId('ffa2'));
  assert.equal(r.json.room.mode, 'ffa');
  assert.equal(client.calls.some((c) => c.name === 'CreateGameSession'), false);
  assert.equal(client.calls[0].input.FilterExpression, 'hasAvailablePlayerSessions=true');
  assert.equal(client.calls[0].input.Limit, 20); // a full page, so a matching room is not cut off
});

test('POST /quickjoin among matches still picks the fullest; mode outranks map', async () => {
  const sessions = [
    gs({ GameSessionId: gsId('a'), CurrentPlayerSessionCount: 7, GameProperties: props('tdm', 'outpost') }),
    gs({ GameSessionId: gsId('b'), CurrentPlayerSessionCount: 5, GameProperties: props('ffa', 'warehouse') }),
    gs({ GameSessionId: gsId('c'), CurrentPlayerSessionCount: 3, GameProperties: props('ffa', 'outpost') }),
    gs({ GameSessionId: gsId('d'), CurrentPlayerSessionCount: 2, GameProperties: props('ffa', 'outpost') }),
  ];
  const join = async (body) => {
    const { call } = makeApp({ SearchGameSessions: () => ({ GameSessions: sessions }), CreatePlayerSession: () => playerSession() });
    return (await call('POST', '/quickjoin', { playerName: 'Park', ...body })).json.room.id;
  };
  assert.equal(await join({ mode: 'ffa' }), gsId('b'), 'fullest ffa room');
  assert.equal(await join({ mode: 'ffa', map: 'outpost' }), gsId('c'), 'both match');
  assert.equal(await join({ mode: 'tdm', map: 'warehouse' }), gsId('a'), 'mode match beats map match');
  assert.equal(await join({ map: 'warehouse' }), gsId('b'), 'map-only preference');
  assert.equal(await join({}), gsId('a'), 'no preference -> fullest');
});

test('POST /quickjoin skips a matching room that fills up and tries the next', async () => {
  const { call } = makeApp({
    SearchGameSessions: () => ({ GameSessions: [
      gs({ GameSessionId: gsId('a'), CurrentPlayerSessionCount: 7, GameProperties: props('ffa', 'warehouse') }),
      gs({ GameSessionId: gsId('b'), CurrentPlayerSessionCount: 6, GameProperties: props('tdm', 'warehouse') }),
      gs({ GameSessionId: gsId('c'), CurrentPlayerSessionCount: 1, GameProperties: props('ffa', 'warehouse') }),
    ] }),
    CreatePlayerSession: (i) => { if (i.GameSessionId === gsId('a')) throw awsError('GameSessionFullException'); return playerSession(); },
  });
  const r = await call('POST', '/quickjoin', { playerName: 'Park', mode: 'ffa' });
  assert.equal(r.json.room.id, gsId('c'));
});

test('POST /quickjoin falls back to any joinable room when none match', async () => {
  const { call, client } = makeApp({
    SearchGameSessions: () => ({ GameSessions: [
      gs({ GameSessionId: gsId('a'), CurrentPlayerSessionCount: 2, GameProperties: props('tdm', 'warehouse') }),
      gs({ GameSessionId: gsId('b'), CurrentPlayerSessionCount: 6, GameProperties: props('tdm', 'outpost') }),
    ] }),
    CreatePlayerSession: () => playerSession(),
  });
  const r = await call('POST', '/quickjoin', { playerName: 'Park', mode: 'ffa', map: 'outpost' });
  assert.equal(r.status, 200);
  assert.equal(r.json.room.id, gsId('b')); // fullest of the non-matching rooms
  assert.deepEqual([r.json.room.mode, r.json.room.map], ['tdm', 'outpost']); // client sees what it actually joined
  assert.equal(client.calls.some((c) => c.name === 'CreateGameSession'), false);
});

test('POST /quickjoin creates the room with the requested mode/map when none is joinable', async () => {
  const { call, client } = makeApp({
    SearchGameSessions: () => ({ GameSessions: [] }),
    CreateGameSession: echoCreate(),
    CreatePlayerSession: () => playerSession(),
  });
  const r = await call('POST', '/quickjoin', { playerName: 'Park', mode: 'ffa', map: 'outpost' });
  assert.equal(r.status, 200);
  assert.deepEqual(client.calls.find((c) => c.name === 'CreateGameSession').input.GameProperties, props('ffa', 'outpost'));
  assert.deepEqual([r.json.room.mode, r.json.room.map], ['ffa', 'outpost']);
  assert.equal(r.json.room.players, 1);
});

test('POST /quickjoin creating a room without preferences uses the defaults; a partial preference defaults the rest', async () => {
  let app = makeApp({ SearchGameSessions: () => ({ GameSessions: [] }), CreateGameSession: echoCreate(), CreatePlayerSession: () => playerSession() });
  let r = await app.call('POST', '/quickjoin', { playerName: 'Park' });
  assert.equal(r.status, 200);
  assert.deepEqual(app.client.calls.find((c) => c.name === 'CreateGameSession').input.GameProperties, props('tdm', 'warehouse'));
  assert.deepEqual([r.json.room.mode, r.json.room.map], ['tdm', 'warehouse']);

  app = makeApp({ SearchGameSessions: () => ({ GameSessions: [] }), CreateGameSession: echoCreate(), CreatePlayerSession: () => playerSession() });
  r = await app.call('POST', '/quickjoin', { playerName: 'Park', map: 'outpost' });
  assert.deepEqual(app.client.calls.find((c) => c.name === 'CreateGameSession').input.GameProperties, props('tdm', 'outpost'));
});

test('POST /quickjoin does not pick a matching room that is still ACTIVATING', async () => {
  const { call, client } = makeApp({
    SearchGameSessions: () => ({ GameSessions: [
      gs({ GameSessionId: gsId('a'), Status: 'ACTIVATING', CurrentPlayerSessionCount: 0, GameProperties: props('ffa', 'warehouse') }),
      gs({ GameSessionId: gsId('b'), CurrentPlayerSessionCount: 3, GameProperties: props('tdm', 'warehouse') }),
    ] }),
    CreatePlayerSession: () => playerSession(),
  });
  const r = await call('POST', '/quickjoin', { playerName: 'Park', mode: 'ffa' });
  assert.equal(r.json.room.id, gsId('b'));
  assert.equal(client.calls.some((c) => c.name === 'CreateGameSession'), false);
});

test('korean names count by character, 16 allowed', async () => {
  const { call } = makeApp({
    SearchGameSessions: () => ({ GameSessions: [gs()] }),
    CreatePlayerSession: () => playerSession(),
  });
  const r = await call('POST', '/quickjoin', { playerName: '가'.repeat(16) });
  assert.equal(r.status, 200);
});

test('unknown AWS error -> 500 INTERNAL without leaking details', async () => {
  const { call } = makeApp({ SearchGameSessions: () => { throw new Error('secret stack detail'); } });
  const orig = console.error; console.error = () => {};
  const r = await call('GET', '/rooms');
  console.error = orig;
  assert.equal(r.status, 500);
  assert.equal(r.json.error, 'INTERNAL');
  assert.ok(!JSON.stringify(r.json).includes('secret'));
});

test('unknown route -> 404, OPTIONS -> 204, CORS only for allowed origins', async () => {
  const { call } = makeApp({ SearchGameSessions: () => ({ GameSessions: [] }) });
  assert.equal((await call('GET', '/nope')).status, 404);
  const ok = await call('OPTIONS', '/rooms', undefined, { origin: 'https://game.example.com' });
  assert.equal(ok.status, 204);
  assert.equal(ok.headers['access-control-allow-origin'], 'https://game.example.com');
  const bad = await call('GET', '/rooms', undefined, { origin: 'https://evil.example.com' });
  assert.equal(bad.headers['access-control-allow-origin'], undefined);
});

test('missing FLEET_ALIAS_ID/FLEET_ID -> 500, FLEET_ID + LOCATION are honoured', async () => {
  const orig = console.error; console.error = () => {};
  let r = await makeApp({}, {}).call('GET', '/rooms');
  console.error = orig;
  assert.equal(r.status, 500);
  const { call, client } = makeApp({ SearchGameSessions: () => ({ GameSessions: [] }) }, { FLEET_ID: 'fleet-1', LOCATION: 'ap-northeast-2' });
  r = await call('GET', '/rooms');
  assert.equal(r.status, 200);
  assert.equal(client.calls[0].input.FleetId, 'fleet-1');
  assert.equal(client.calls[0].input.AliasId, undefined);
  assert.equal(client.calls[0].input.Location, 'ap-northeast-2');
});

test('helpers', () => {
  assert.equal(cleanText('  a\tb​<c>\n'), 'a bc');
  assert.ok(Array.from(autoRoomName('x'.repeat(16), () => 0)).length <= 24);
});
