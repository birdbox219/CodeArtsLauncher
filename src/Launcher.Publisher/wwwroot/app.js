// Birdbox publishing panel — front end.
//
// Two rules it inherits from the launcher: never show a number the server did not report (an
// unknown size stays "—", the progress bar goes indeterminate rather than guessing a percentage),
// and never hide a failure — butler's own words go straight into the log.

const $ = (id) => document.getElementById(id);

const state = {
  games: [],
  selected: null,
  jobId: null,
  events: null,      // EventSource
  running: false,
  report: null,
};

// =============================== helpers ===============================

async function api(path, options = {}) {
  const res = await fetch(path, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      // Required by the server on every POST: a cross-site form cannot set a custom header, so
      // another page in this browser cannot publish a build behind your back.
      'X-Publisher-Panel': '1',
      ...(options.headers || {}),
    },
  });

  let body = null;
  try { body = await res.json(); } catch { /* empty body */ }

  if (!res.ok) throw new Error(body?.error || `${res.status} ${res.statusText}`);
  return body;
}

function bytes(n) {
  if (!n || n <= 0) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let v = n, u = 0;
  while (v >= 1024 && u < units.length - 1) { v /= 1024; u++; }
  return u === 0 ? `${n} B` : `${v.toFixed(v >= 100 ? 0 : 1)} ${units[u]}`;
}

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

// =============================== setup / games ===============================

async function loadState() {
  const s = await api('/api/state');
  $('topbar-meta').textContent = `profile ${s.profile}   ·   channel ${s.defaultChannel}   ·   key ${s.hasButlerCredentials ? 'found' : 'missing'}`;

  if (!s.hasButlerCredentials) {
    $('key-warning').textContent = s.credentialsHint;
    $('key-warning').classList.remove('hidden');
  }

  if (s.runningJobId) attach(s.runningJobId, true);
}

async function loadGames(refresh = false) {
  $('games-hint').textContent = refresh ? 'Re-reading itch.io and every channel…' : 'Loading from itch.io…';
  $('games-hint').classList.remove('hidden');

  try {
    const catalog = await api(`/api/games?refresh=${refresh}`);
    state.games = catalog.games;
    renderGames(catalog);
  } catch (err) {
    $('games-hint').textContent = `Could not load the game list: ${err.message}`;
  }
}

function renderGames(catalog) {
  const list = $('games-list');
  list.innerHTML = '';

  $('games-count').textContent = `(${catalog.games.length})`;

  const notes = [];
  if (catalog.fromCache) notes.push('Showing the last synced catalog — itch.io was not reachable.');
  if (catalog.diagnostic) notes.push(catalog.diagnostic);
  $('games-hint').textContent = notes.join(' ');
  $('games-hint').classList.toggle('hidden', notes.length === 0);

  for (const game of catalog.games) {
    const li = el('li', 'game');
    li.dataset.slug = game.slug;

    const img = el('img');
    if (game.coverImageUrl) img.src = game.coverImageUrl;
    img.alt = '';
    li.append(img);

    const grow = el('div', 'grow');
    grow.append(el('div', 'name', game.title || game.slug));

    const head = game.channels[0];
    grow.append(el('div', 'sub', head
      ? `${head.name} · build ${head.buildId}${head.version ? ' · ' + head.version : ''}`
      : `${game.owner}/${game.slug}`));
    li.append(grow);

    if (game.isCollaboration) li.append(el('span', 'tag', 'collab'));
    li.append(el('span', game.isPublished ? 'tag live' : 'tag none',
      game.isPublished ? `${game.channels.length} ch` : 'no channel'));

    li.addEventListener('click', () => select(game.slug));
    list.append(li);
  }

  if (state.selected) {
    const again = catalog.games.find((g) => g.slug === state.selected.slug);
    if (again) select(again.slug);
  }
}

function select(slug) {
  const game = state.games.find((g) => g.slug === slug);
  if (!game) return;

  state.selected = game;

  for (const li of document.querySelectorAll('.game'))
    li.classList.toggle('active', li.dataset.slug === slug);

  $('no-selection').classList.add('hidden');
  $('form').classList.remove('hidden');

  $('sel-title').textContent = game.title || game.slug;
  $('sel-target').textContent = `${game.owner}/${game.slug}`;
  $('sel-cover').src = game.coverImageUrl || '';

  const channels = $('sel-channels');
  channels.innerHTML = '';
  if (game.channels.length === 0) {
    channels.append(el('span', 'tag none', 'never pushed'));
  } else {
    for (const c of game.channels) {
      channels.append(el('span', 'tag live',
        `${c.name} · ${c.buildId}${c.version ? ' · ' + c.version : ''}`));
    }
  }
  if (game.channelError) channels.append(el('span', 'tag none', game.channelError));

  $('channel').value = game.channels.length === 1 ? game.channels[0].name : game.defaultChannel;
  $('version').value = '';
  $('version').placeholder = game.channels[0]?.version
    ? `now ${game.channels[0].version} — the next one, e.g. 1.0.1`
    : '1.0.0';

  updateChannelNote();
  updateButtons();
}

// =============================== folder ===============================

let inspectTimer = null;

function scheduleInspect() {
  clearTimeout(inspectTimer);
  inspectTimer = setTimeout(inspectFolder, 350);
}

async function inspectFolder() {
  const folder = $('folder').value.trim();
  const box = $('folder-report');

  if (!folder) {
    state.report = null;
    box.classList.add('hidden');
    updateButtons();
    return;
  }

  try {
    const report = await api('/api/inspect', {
      method: 'POST',
      body: JSON.stringify({ folder }),
    });

    state.report = report;
    box.innerHTML = '';

    if (report.canPush) {
      box.append(el('div', 'line',
        `${report.fileCount} files · ${bytes(report.totalBytes)}` +
        (report.executable ? ` · exe ${report.executable}` : '')));
      box.append(el('div', 'line', report.path));
    }
    for (const p of report.problems) box.append(el('div', 'problem', p));
    for (const w of report.warnings) box.append(el('div', 'warning', `Warning: ${w}`));

    box.classList.remove('hidden');
  } catch (err) {
    state.report = null;
    box.textContent = err.message;
    box.classList.remove('hidden');
  }

  updateButtons();
}

function updateChannelNote() {
  const note = $('channel-note');
  const game = state.selected;
  const channel = $('channel').value.trim();

  if (!game || !channel) { note.classList.add('hidden'); return; }

  const exists = game.channels.some((c) => c.name.toLowerCase() === channel.toLowerCase());

  if (game.channels.length === 0) {
    note.textContent =
      `First push for this game: it creates the \`${channel}\` channel. Players can install it, ` +
      `but there is nothing to patch from yet — the second push to this same channel is the one ` +
      `that produces a delta.`;
    note.classList.remove('hidden');
  } else if (!exists) {
    note.textContent =
      `\`${channel}\` is a new channel for this game. A new channel starts its own patch chain from ` +
      `zero, so the first push to it is a full download. Existing: ` +
      game.channels.map((c) => c.name).join(', ') + '.';
    note.classList.remove('hidden');
  } else {
    note.textContent = '';
    note.classList.add('hidden');
  }
}

function updateButtons() {
  const ready = !!state.selected && !!state.report?.canPush && !state.running;
  $('publish-btn').disabled = !ready;
  $('dryrun-btn').disabled = !ready;
  $('cancel-btn').classList.toggle('hidden', !state.running);
}

// =============================== push ===============================

async function push(dryRun) {
  if (!state.selected || !state.report?.canPush) return;

  $('push').classList.remove('hidden');
  $('log').innerHTML = '';
  $('outcome').classList.add('hidden');
  setProgress(null, dryRun ? 'listing files…' : 'starting butler…');

  state.running = true;
  updateButtons();

  try {
    const { jobId } = await api('/api/push', {
      method: 'POST',
      body: JSON.stringify({
        game: `${state.selected.owner}/${state.selected.slug}`,
        channel: $('channel').value.trim(),
        folder: $('folder').value.trim(),
        version: $('version').value.trim(),
        dryRun,
        onlyIfChanged: $('if-changed').checked,
      }),
    });

    attach(jobId, false);
  } catch (err) {
    appendLog('error', err.message);
    state.running = false;
    updateButtons();
    setProgress(0, 'failed');
  }
}

function attach(jobId, replaying) {
  state.jobId = jobId;
  state.running = true;
  updateButtons();

  $('push').classList.remove('hidden');
  if (replaying) {
    $('log').innerHTML = '';
    setProgress(null, 'a push is already running — reattached');
  }

  state.events?.close();
  const source = new EventSource(`/api/push/${jobId}/events`);
  state.events = source;

  source.onmessage = (message) => {
    const e = JSON.parse(message.data);
    if (e.kind === 'progress') {
      setProgress(e.progress, progressText(e));
    } else {
      appendLog(e.kind, e.message);
    }
  };

  source.addEventListener('done', (message) => {
    source.close();
    state.events = null;
    state.running = false;
    updateButtons();
    renderOutcome(JSON.parse(message.data));
  });

  source.onerror = () => {
    // The stream ends by closing, so an error only matters while the job is still open.
    if (state.running) appendLog('warning', 'Lost the event stream. Reload to reattach; the push keeps going.');
  };
}

function progressText(e) {
  const parts = [`${Math.round(e.progress * 100)}%`];
  if (e.bps > 0) parts.push(`${bytes(e.bps)}/s`);
  if (e.etaSeconds > 0) parts.push(`${Math.round(e.etaSeconds)}s left`);
  return parts.join('   ·   ');
}

function setProgress(fraction, text) {
  const bar = $('progress-bar');
  if (fraction === null || fraction === undefined) {
    bar.classList.add('indeterminate');
    bar.style.width = '';
  } else {
    bar.classList.remove('indeterminate');
    bar.style.width = `${Math.round(fraction * 100)}%`;
  }
  $('progress-text').textContent = text;
}

function appendLog(kind, message) {
  if (!message) return;
  const line = el('div', kind === 'log' ? '' : kind, message);
  if (/Re-used/i.test(message)) line.className = 'delta';
  $('log').append(line);
  $('log').scrollTop = $('log').scrollHeight;
}

function renderOutcome(job) {
  const box = $('outcome');
  const o = job.outcome;
  box.innerHTML = '';

  if (!o) { box.classList.add('hidden'); return; }

  box.className = `outcome ${o.success ? 'good' : 'bad'}`;

  if (!o.success) {
    box.append(el('div', 'headline', 'Push failed'));
    box.append(el('div', '', o.error || 'butler did not say why. The log above is verbatim.'));
  } else if (o.dryRun) {
    box.append(el('div', 'headline', 'Dry run only — nothing was uploaded'));
    box.append(el('div', '', 'The listing above is what a real push would send.'));
    setProgress(1, 'dry run finished');
  } else if (o.skipped) {
    box.append(el('div', 'headline', 'Nothing changed — no build created'));
    box.append(el('div', '', 'butler skipped the push because the folder matches the current build (--if-changed).'));
    setProgress(1, 'skipped');
  } else {
    box.append(el('div', 'headline', `Published — build ${o.buildId || '—'}`));
    if (o.delta) {
      box.append(el('div', 'delta',
        `Re-used ${o.delta.reusedPercent}% of the previous build` +
        (o.delta.freshData ? `, uploaded ${o.delta.freshData} of new data` : '') + '.'));
      box.append(el('div', '', 'That re-use is the delta. The launcher now offers this as a patch instead of a full download.'));
    } else {
      box.append(el('div', '',
        'butler reported no re-use figure, which is expected on the first build of a channel: ' +
        'there was nothing to diff against. Push this same channel again after a change and the ' +
        'next one will be a patch.'));
    }
    setProgress(1, 'done');
    loadGames(true);
  }

  box.classList.remove('hidden');
}

async function cancel() {
  if (!state.jobId) return;
  try { await api(`/api/push/${state.jobId}/cancel`, { method: 'POST' }); }
  catch (err) { appendLog('warning', `Could not cancel: ${err.message}`); }
}

// =============================== folder picker ===============================

let browserPath = '';

async function openBrowser() {
  $('browser').classList.remove('hidden');
  await showFolder($('folder').value.trim() || '');
}

async function showFolder(path) {
  try {
    const listing = await api(`/api/browse?path=${encodeURIComponent(path)}`);
    browserPath = listing.path;

    $('browser-path').textContent = listing.path || 'This PC';
    $('browser-up').disabled = !listing.parent && !listing.path;
    $('browser-error').textContent = listing.error || '';
    $('browser-files').textContent = listing.path
      ? (listing.hasFiles ? 'This folder has files in it.' : 'No files directly in this folder.')
      : '';
    $('browser-use').disabled = !listing.path;

    const list = $('browser-list');
    list.innerHTML = '';
    for (const dir of listing.directories) {
      const li = el('li', '', dir.name);
      li.addEventListener('click', () => showFolder(dir.path));
      list.append(li);
    }
    list.dataset.parent = listing.parent || '';
  } catch (err) {
    $('browser-error').textContent = err.message;
  }
}

// =============================== wiring ===============================

$('refresh-btn').addEventListener('click', () => loadGames(true));
$('folder').addEventListener('input', scheduleInspect);
$('channel').addEventListener('input', updateChannelNote);
$('dryrun-btn').addEventListener('click', () => push(true));
$('publish-btn').addEventListener('click', () => push(false));
$('cancel-btn').addEventListener('click', cancel);
$('browse-btn').addEventListener('click', openBrowser);
$('browser-close').addEventListener('click', () => $('browser').classList.add('hidden'));
$('browser-up').addEventListener('click', () => showFolder($('browser-list').dataset.parent || ''));
$('browser-use').addEventListener('click', () => {
  $('folder').value = browserPath;
  $('browser').classList.add('hidden');
  inspectFolder();
});

loadState();
loadGames(false);
