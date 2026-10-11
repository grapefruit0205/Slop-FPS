// Lobby API logic. Framework-free: `createApp({ client, env, ... })` returns
// `handle(httpApiV2Event) -> { statusCode, headers, body }`.
// The GameLift client is injected so unit tests can pass a fake `send`.
import { randomUUID } from 'node:crypto';
import {
  CreateGameSessionCommand,
  CreatePlayerSessionCommand,
  DescribeGameSessionsCommand,
  SearchGameSessionsCommand,
} from '@aws-sdk/client-gamelift';

// ---- limits (see API contract in README) ---------------------------------
export const PLAYER_NAME_MAX = 16;
export const ROOM_NAME_MAX = 24;
const ROOM_ID_MAX = 256;
const BODY_MAX_BYTES = 4096;
const LIST_LIMIT = 50;
const SEARCH_PAGE_SIZE = 20; // SearchGameSessions never returns more than 20 per page
const QUICKJOIN_CANDIDATES = 3; // how many rooms quickjoin tries to join before creating one
const DEFAULT_ACTIVATION_WAIT_MS = 8000;
const POLL_INTERVAL_MS = 400;

// Game modes and maps a room can be created with. Stored as GameProperties `mode` / `map`,
// which the dedicated server reads in OnStartGameSession. Adding a map = one entry here.
export const MODES = ['tdm', 'ffa', 'elim']; // tdm = 4v4 team deathmatch, ffa = 8-player free-for-all, elim = 4v4 elimination rounds (first to 7)
export const MAPS = ['warehouse', 'outpost'];
export const DEFAULT_MODE = 'tdm';
export const DEFAULT_MAP = 'warehouse';

/** Error carrying the HTTP status + API error code. Messages are safe to show to clients. */
export class ApiError extends Error {
  constructor(status, code, message) {
    super(message ?? code);
    this.status = status;
    this.code = code;
  }
}
const badRequest = (m) => new ApiError(400, 'BAD_REQUEST', m);

// ---- input sanitising ------------------------------------------------------
// Control chars (Cc), invisible/format chars (Cf: zero-width, bidi overrides),
// line/paragraph separators, and < > are stripped. Tabs/newlines become a space first.
const STRIP_RE = /[\p{Cc}\p{Cf}\p{Zl}\p{Zp}<>]/gu;
export function cleanText(value) {
  return value.replace(/[\t\n\r]+/g, ' ').replace(STRIP_RE, '').replace(/\s+/g, ' ').trim();
}
const len = (s) => Array.from(s).length; // count code points (Korean/emoji safe)

export function validatePlayerName(v) {
  if (typeof v !== 'string') throw badRequest('playerName is required');
  const s = cleanText(v);
  if (len(s) < 1 || len(s) > PLAYER_NAME_MAX) {
    throw badRequest(`playerName must be 1-${PLAYER_NAME_MAX} characters`);
  }
  return s;
}

export function validateRoomName(v) {
  if (v === undefined || v === null) return '';
  if (typeof v !== 'string') throw badRequest('name must be a string');
  const s = cleanText(v);
  if (len(s) > ROOM_NAME_MAX) throw badRequest(`name must be at most ${ROOM_NAME_MAX} characters`);
  return s;
}

export function validateRoomId(v) {
  if (typeof v !== 'string' || !v) throw badRequest('roomId is required');
  if (v.length > ROOM_ID_MAX || !v.startsWith('arn:aws:gamelift:') || !/^[A-Za-z0-9:/_.-]+$/.test(v)) {
    throw badRequest('roomId is invalid');
  }
  return v;
}

/** Optional enum field: undefined/null -> undefined (caller picks the default); anything else must be listed. */
export function validateChoice(v, allowed, field) {
  if (v === undefined || v === null) return undefined;
  if (typeof v !== 'string' || !allowed.includes(v)) {
    throw badRequest(`${field} 값이 올바르지 않습니다. 사용 가능: ${allowed.join(', ')}`);
  }
  return v;
}

/** "<playerName>의 작전 #NNN", clipped so the whole name stays within ROOM_NAME_MAX. */
export function autoRoomName(playerName, rng = Math.random) {
  const suffix = `의 작전 #${100 + Math.floor(rng() * 900)}`;
  const room = Math.max(1, ROOM_NAME_MAX - len(suffix));
  return Array.from(playerName).slice(0, room).join('') + suffix;
}

// ---- response helpers --------------------------------------------------------
const gameProp = (gs, key) => gs.GameProperties?.find((p) => p.Key === key)?.Value;
/** Read a listed value back from GameProperties (case-insensitive, so older "Warehouse" sessions still map); else the default. */
function readChoice(gs, key, allowed, fallback) {
  const v = String(gameProp(gs, key) ?? '').toLowerCase();
  return allowed.includes(v) ? v : fallback;
}
export const roomMode = (gs) => readChoice(gs, 'mode', MODES, DEFAULT_MODE);
export const roomMap = (gs) => readChoice(gs, 'map', MAPS, DEFAULT_MAP);

/** Order sessions so those matching the wanted mode/map come first (mode outranks map).
 * The sort is stable, so the players-desc order from search() is kept inside each tier. */
export function rankByPreference(sessions, want) {
  const score = (gs) => (want.mode && roomMode(gs) === want.mode ? 2 : 0) + (want.map && roomMap(gs) === want.map ? 1 : 0);
  return [...sessions].sort((a, b) => score(b) - score(a));
}

export function toRoom(gs, playersOverride) {
  return {
    id: gs.GameSessionId,
    name: gs.Name ?? '',
    mode: roomMode(gs),
    map: roomMap(gs),
    players: playersOverride ?? gs.CurrentPlayerSessionCount ?? 0,
    maxPlayers: gs.MaximumPlayerSessionCount ?? 0,
    status: gs.Status ?? 'ACTIVE',
  };
}

const errName = (err) => err?.name ?? err?.constructor?.name ?? '';
const isRoomFullErr = (e) => ['GameSessionFullException', 'InvalidGameSessionStatusException'].includes(errName(e));

/** Map any thrown value to an ApiError. Unknown errors are logged but never leaked. */
export function toApiError(err) {
  if (err instanceof ApiError) return err;
  switch (errName(err)) {
    case 'GameSessionFullException':
    case 'InvalidGameSessionStatusException':
      return new ApiError(409, 'ROOM_FULL', 'Room is full or not accepting players');
    case 'NotFoundException':
      return new ApiError(404, 'NOT_FOUND', 'Room not found');
    case 'FleetCapacityExceededException':
    case 'OutOfCapacityException':
      return new ApiError(503, 'FLEET_FULL', 'No server slot is free right now');
    case 'InvalidFleetStatusException':
      return new ApiError(503, 'SERVER_STARTING', 'Game servers are starting, try again shortly');
    default:
      console.error(JSON.stringify({ level: 'error', name: errName(err), message: String(err?.message ?? err) }));
      return new ApiError(500, 'INTERNAL', 'Internal error');
  }
}

// ---- app ---------------------------------------------------------------------
export function createApp({
  client,
  env = process.env,
  sleep = (ms) => new Promise((r) => setTimeout(r, ms)),
  now = Date.now,
  rng = Math.random,
} = {}) {
  const maxPlayers = Number.parseInt(env.MAX_PLAYERS ?? '8', 10) || 8;
  const waitMs = Number.parseInt(env.ACTIVATION_WAIT_MS ?? '', 10) || DEFAULT_ACTIVATION_WAIT_MS;
  const allowedOrigins = (env.ALLOWED_ORIGINS ?? '').split(',').map((s) => s.trim()).filter(Boolean);

  // Alias is preferred (blue/green fleet swaps); FLEET_ID is the fallback.
  const target = env.FLEET_ALIAS_ID ? { AliasId: env.FLEET_ALIAS_ID } : env.FLEET_ID ? { FleetId: env.FLEET_ID } : null;
  const location = env.LOCATION ? { Location: env.LOCATION } : {};

  const send = (cmd) => client.send(cmd);
  const needTarget = () => {
    if (!target) {
      console.error(JSON.stringify({ level: 'error', message: 'FLEET_ALIAS_ID or FLEET_ID must be set' }));
      throw new ApiError(500, 'INTERNAL', 'Internal error');
    }
    return target;
  };

  // -- GameLift operations ---------------------------------------------------
  /** Search up to `max` sessions, following NextToken (GameLift caps pages at 20). */
  async function search({ filter, max }) {
    const out = [];
    let token;
    do {
      const res = await send(new SearchGameSessionsCommand({
        ...needTarget(),
        ...location,
        ...(filter ? { FilterExpression: filter } : {}),
        SortExpression: 'playerSessionCount DESC',
        Limit: Math.min(SEARCH_PAGE_SIZE, max - out.length),
        ...(token ? { NextToken: token } : {}),
      }));
      out.push(...(res.GameSessions ?? []));
      token = res.NextToken;
    } while (token && out.length < max);
    // Results are already sorted server-side per page; re-sort globally to be safe across pages.
    return out
      .filter((gs) => gs.Status === 'ACTIVE' || gs.Status === 'ACTIVATING')
      .sort((a, b) => (b.CurrentPlayerSessionCount ?? 0) - (a.CurrentPlayerSessionCount ?? 0))
      .slice(0, max);
  }

  async function describe(gameSessionId) {
    // GameSessionId alone: the API rejects it together with AliasId/FleetId ("one and only one of").
    const res = await send(new DescribeGameSessionsCommand({ GameSessionId: gameSessionId }));
    return res.GameSessions?.[0] ?? null;
  }

  /** CreateGameSession, then poll until ACTIVE (the server process must call ActivateGameSession). */
  async function createActiveSession(name, { mode, map }) {
    const created = await send(new CreateGameSessionCommand({
      ...needTarget(),
      ...location,
      Name: name,
      MaximumPlayerSessionCount: maxPlayers,
      GameProperties: [{ Key: 'mode', Value: mode }, { Key: 'map', Value: map }],
      IdempotencyToken: randomUUID(),
    }));
    let gs = created.GameSession;
    if (!gs?.GameSessionId) throw new ApiError(500, 'INTERNAL', 'Internal error');
    const deadline = now() + waitMs;
    while (gs.Status !== 'ACTIVE') {
      if (gs.Status === 'TERMINATING' || gs.Status === 'TERMINATED') {
        throw new ApiError(500, 'INTERNAL', 'Game session failed to start');
      }
      if (now() >= deadline) throw new ApiError(503, 'SERVER_STARTING', 'Server is still starting, try again shortly');
      await sleep(POLL_INTERVAL_MS);
      gs = (await describe(gs.GameSessionId)) ?? gs;
    }
    return gs;
  }

  /** CreatePlayerSession and shape the `connect` block. DnsName (TLS cert) preferred over IP. */
  async function joinSession(gs, playerName) {
    const res = await send(new CreatePlayerSessionCommand({
      GameSessionId: gs.GameSessionId,
      PlayerId: randomUUID(), // PlayerId is mandatory in the API; players are anonymous here
      PlayerData: playerName,
    }));
    const ps = res.PlayerSession;
    const host = ps?.DnsName || gs.DnsName || ps?.IpAddress || gs.IpAddress;
    if (!ps?.PlayerSessionId || !host) throw new ApiError(500, 'INTERNAL', 'Internal error');
    return {
      room: toRoom(gs, Math.min((gs.CurrentPlayerSessionCount ?? 0) + 1, gs.MaximumPlayerSessionCount ?? maxPlayers)),
      connect: { host, port: ps.Port ?? gs.Port ?? 7770, playerSessionId: ps.PlayerSessionId },
    };
  }

  /** `want.mode` / `want.map` may be undefined (no preference) -> defaults. */
  async function createRoomAndJoin(roomName, playerName, want = {}) {
    const props = { mode: want.mode ?? DEFAULT_MODE, map: want.map ?? DEFAULT_MAP };
    const gs = await createActiveSession(roomName || autoRoomName(playerName, rng), props);
    return joinSession(gs, playerName);
  }

  // -- route handlers --------------------------------------------------------
  const readWanted = (body) => ({
    mode: validateChoice(body.mode, MODES, 'mode'),
    map: validateChoice(body.map, MAPS, 'map'),
  });

  async function listRooms() {
    const sessions = await search({ max: LIST_LIMIT });
    return { rooms: sessions.map((gs) => toRoom(gs)) };
  }

  async function createRoom(body) {
    const playerName = validatePlayerName(body.playerName);
    const roomName = validateRoomName(body.name);
    return createRoomAndJoin(roomName, playerName, readWanted(body));
  }

  async function joinRoom(body) {
    const roomId = validateRoomId(body.roomId);
    const playerName = validatePlayerName(body.playerName);
    // Describe via alias/fleet first: scopes the id to our fleet and gives us the room data.
    const gs = await describe(roomId);
    if (!gs || gs.Status === 'TERMINATING' || gs.Status === 'TERMINATED') throw new ApiError(404, 'NOT_FOUND', 'Room not found');
    if (gs.Status === 'ACTIVATING') throw new ApiError(503, 'SERVER_STARTING', 'Room is still starting');
    if ((gs.CurrentPlayerSessionCount ?? 0) >= (gs.MaximumPlayerSessionCount ?? maxPlayers)) {
      throw new ApiError(409, 'ROOM_FULL', 'Room is full');
    }
    return joinSession(gs, playerName);
  }

  async function quickJoin(body) {
    const playerName = validatePlayerName(body.playerName);
    const want = readWanted(body); // preferences only: matching room -> any open room -> new room
    // Search a whole page, not just the fullest few, so a matching room is found even if it is nearly empty.
    const open = (await search({ filter: 'hasAvailablePlayerSessions=true', max: SEARCH_PAGE_SIZE }))
      .filter((gs) => gs.Status === 'ACTIVE'); // ACTIVATING sessions cannot take players yet
    const candidates = rankByPreference(open, want).slice(0, QUICKJOIN_CANDIDATES);
    for (const gs of candidates) {
      try {
        return await joinSession(gs, playerName);
      } catch (err) {
        // Lost a race for the last slot / session ended: try the next candidate.
        if (isRoomFullErr(err) || errName(err) === 'NotFoundException') continue;
        throw err;
      }
    }
    return createRoomAndJoin('', playerName, want);
  }

  // -- HTTP plumbing -----------------------------------------------------------
  function corsHeaders(origin) {
    if (!origin) return {};
    const ok = allowedOrigins.includes('*') || allowedOrigins.includes(origin);
    if (!ok) return {};
    return {
      'access-control-allow-origin': allowedOrigins.includes('*') ? '*' : origin,
      'access-control-allow-methods': 'GET,POST,OPTIONS',
      'access-control-allow-headers': 'content-type',
      'access-control-max-age': '600',
      vary: 'Origin',
    };
  }

  function parseBody(event) {
    if (!event.body) return {};
    let raw = event.body;
    if (event.isBase64Encoded) raw = Buffer.from(raw, 'base64').toString('utf8');
    if (Buffer.byteLength(raw) > BODY_MAX_BYTES) throw badRequest('body too large');
    let parsed;
    try {
      parsed = JSON.parse(raw);
    } catch {
      throw badRequest('body must be valid JSON');
    }
    if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) throw badRequest('body must be a JSON object');
    return parsed;
  }

  async function route(event) {
    const method = (event.requestContext?.http?.method ?? event.httpMethod ?? '').toUpperCase();
    const path = (event.rawPath ?? event.path ?? '/').replace(/\/+$/, '') || '/';
    if (method === 'GET' && path === '/rooms') return listRooms();
    if (method === 'POST' && path === '/rooms') return createRoom(parseBody(event));
    if (method === 'POST' && path === '/rooms/join') return joinRoom(parseBody(event));
    if (method === 'POST' && path === '/quickjoin') return quickJoin(parseBody(event));
    throw new ApiError(404, 'NOT_FOUND', 'Unknown route');
  }

  return async function handle(event) {
    const origin = event.headers?.origin ?? event.headers?.Origin;
    const base = { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store', ...corsHeaders(origin) };
    const method = (event.requestContext?.http?.method ?? event.httpMethod ?? '').toUpperCase();
    if (method === 'OPTIONS') return { statusCode: 204, headers: base, body: '' };
    try {
      const data = await route(event);
      return { statusCode: 200, headers: base, body: JSON.stringify(data) };
    } catch (raw) {
      const err = toApiError(raw);
      return {
        statusCode: err.status,
        headers: base,
        body: JSON.stringify({ error: err.code, message: err.message }),
      };
    }
  };
}
