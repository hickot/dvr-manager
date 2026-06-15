const api = '/api/cameras';
const grid = document.querySelector('#camera-grid');
const empty = document.querySelector('#empty-state');
const notice = document.querySelector('#notice');
const dialog = document.querySelector('#camera-dialog');
const form = document.querySelector('#camera-form');
const directoryDialog = document.querySelector('#directory-dialog');
let cameras = [];
let systemStatus = null;
let currentDirectory = null;
let parentDirectory = null;

async function request(url, options = {}) {
  const response = await fetch(url, {
    headers: { 'Content-Type': 'application/json', ...options.headers },
    ...options
  });
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    const validation = body.errors ? Object.values(body.errors).flat().join(' ') : '';
    throw new Error(validation || body.detail || `Erro ${response.status}`);
  }
  return response.status === 204 ? null : response.json();
}

async function load() {
  try {
    [cameras] = await Promise.all([request(api), loadSystem()]);
    render();
    notice.classList.add('hidden');
  } catch (error) {
    showNotice(error.message);
  }
}

async function loadSystem() {
  const status = await request('/api/system');
  systemStatus = status;
  document.querySelector('#active-count').textContent = status.activeRecordings;
  document.querySelector('#storage-usage').textContent = `${status.usedStorageGb} / ${status.maxStorageGb} GB`;
  const ffmpeg = document.querySelector('#ffmpeg-status');
  ffmpeg.textContent = status.ffmpegAvailable ? 'Disponível' : 'Não encontrado';
  ffmpeg.style.color = status.ffmpegAvailable ? 'var(--accent)' : 'var(--danger)';
}

function render() {
  document.querySelector('#camera-count').textContent = cameras.length;
  empty.classList.toggle('hidden', cameras.length !== 0);
  grid.innerHTML = cameras.map(camera => `
    <article class="camera-card">
      <div class="camera-preview">
        <div class="camera-icon">◉</div>
        <span class="status ${camera.recording.state}">${statusLabel(camera.recording.state)}</span>
      </div>
      <div class="camera-body">
        <h3>${escapeHtml(camera.name)}</h3>
        <div class="stream-url" title="${escapeHtml(camera.rtspUrl)}">${escapeHtml(camera.rtspUrl)}</div>
        <div class="camera-meta"><span>${camera.rtspTransport.toUpperCase()} · ${camera.segmentMinutes} min</span><span>${camera.enabled ? 'Automática' : 'Manual'}</span></div>
        <div class="card-actions">
          <button class="secondary" onclick="editCamera('${camera.id}')">Editar</button>
          <button class="secondary" onclick="toggleRecording('${camera.id}', '${camera.recording.state}')">${camera.recording.state === 'recording' ? 'Parar' : 'Gravar'}</button>
          <button class="danger" onclick="removeCamera('${camera.id}')">Remover</button>
        </div>
      </div>
    </article>`).join('');
}

function openForm(camera = null) {
  form.reset();
  document.querySelector('#camera-id').value = camera?.id || '';
  document.querySelector('#form-title').textContent = camera ? 'Editar câmera' : 'Nova câmera';
  document.querySelector('#name').value = camera?.name || '';
  document.querySelector('#rtsp-url').value = camera?.rtspUrl || '';
  document.querySelector('#username').value = camera?.username || '';
  document.querySelector('#password').placeholder = camera?.hasPassword ? 'Manter senha atual' : 'Senha da câmera';
  document.querySelector('#segment-minutes').value = camera?.segmentMinutes || 15;
  document.querySelector('#rtsp-transport').value = camera?.rtspTransport || 'auto';
  document.querySelector('#storage-path').value = camera?.storagePath || '';
  document.querySelector('#storage-path-help').textContent = systemStatus
    ? `Deixe vazio para usar: ${systemStatus.storagePath}`
    : 'Deixe vazio para usar o diretório padrão.';
  document.querySelector('#enabled').checked = camera?.enabled ?? true;
  document.querySelector('#form-error').classList.add('hidden');
  dialog.showModal();
}

async function openDirectoryPicker() {
  const selectedPath = document.querySelector('#storage-path').value;
  const initialPath = selectedPath || systemStatus?.storagePath || '';
  directoryDialog.showModal();
  await loadDirectory(initialPath);
}

async function loadDirectory(path = '') {
  const error = document.querySelector('#directory-error');
  error.classList.add('hidden');

  try {
    const query = path ? `?path=${encodeURIComponent(path)}` : '';
    const listing = await request(`/api/directories${query}`);
    currentDirectory = listing.currentPath;
    parentDirectory = listing.parentPath;
    document.querySelector('#directory-current').textContent = currentDirectory || 'Unidades disponíveis';
    document.querySelector('#directory-up').disabled = !currentDirectory;
    document.querySelector('#select-directory').disabled = !currentDirectory;

    const list = document.querySelector('#directory-list');
    list.innerHTML = listing.directories.length
      ? listing.directories.map(directory => `
          <button type="button" class="directory-entry" data-path="${escapeAttribute(directory.path)}">
            <span aria-hidden="true">&#128193;</span><span>${escapeHtml(directory.name)}</span>
          </button>`).join('')
      : '<div class="directory-empty">Esta pasta não contém outras pastas.</div>';

    list.querySelectorAll('.directory-entry').forEach(button => {
      button.addEventListener('click', () => loadDirectory(button.dataset.path));
    });
  } catch (loadError) {
    error.textContent = loadError.message;
    error.classList.remove('hidden');
  }
}

form.addEventListener('submit', async event => {
  event.preventDefault();
  const id = document.querySelector('#camera-id').value;
  const password = document.querySelector('#password').value;
  const payload = {
    name: document.querySelector('#name').value,
    rtspUrl: document.querySelector('#rtsp-url').value,
    username: document.querySelector('#username').value,
    password: password || null,
    enabled: document.querySelector('#enabled').checked,
    segmentMinutes: Number(document.querySelector('#segment-minutes').value),
    rtspTransport: document.querySelector('#rtsp-transport').value,
    storagePath: document.querySelector('#storage-path').value.trim()
  };

  try {
    await request(id ? `${api}/${id}` : api, { method: id ? 'PUT' : 'POST', body: JSON.stringify(payload) });
    dialog.close();
    await load();
  } catch (error) {
    const target = document.querySelector('#form-error');
    target.textContent = error.message;
    target.classList.remove('hidden');
  }
});

window.editCamera = id => openForm(cameras.find(camera => camera.id === id));
window.toggleRecording = async (id, state) => {
  try { await request(`${api}/${id}/${state === 'recording' ? 'stop' : 'start'}`, { method: 'POST' }); await load(); }
  catch (error) { showNotice(error.message); }
};
window.removeCamera = async id => {
  const camera = cameras.find(item => item.id === id);
  if (!confirm(`Remover a câmera "${camera.name}"? Os vídeos já gravados serão mantidos.`)) return;
  try { await request(`${api}/${id}`, { method: 'DELETE' }); await load(); }
  catch (error) { showNotice(error.message); }
};

function statusLabel(status) { return ({ recording: 'Gravando', stopped: 'Parada', error: 'Erro' })[status] || status; }
function showNotice(message) { notice.textContent = message; notice.classList.remove('hidden'); }
function escapeHtml(value) { const div = document.createElement('div'); div.textContent = value; return div.innerHTML; }
function escapeAttribute(value) { return escapeHtml(value).replaceAll('`', '&#96;'); }

document.querySelector('#add-camera').addEventListener('click', () => openForm());
document.querySelector('#refresh').addEventListener('click', load);
document.querySelector('#close-dialog').addEventListener('click', () => dialog.close());
document.querySelector('#cancel-dialog').addEventListener('click', () => dialog.close());
document.querySelector('#choose-storage-path').addEventListener('click', openDirectoryPicker);
document.querySelector('#close-directory-dialog').addEventListener('click', () => directoryDialog.close());
document.querySelector('#cancel-directory').addEventListener('click', () => directoryDialog.close());
document.querySelector('#directory-up').addEventListener('click', () => loadDirectory(parentDirectory || ''));
document.querySelector('#select-directory').addEventListener('click', () => {
  document.querySelector('#storage-path').value = currentDirectory || '';
  directoryDialog.close();
});
document.querySelector('#use-default-directory').addEventListener('click', () => {
  document.querySelector('#storage-path').value = '';
  directoryDialog.close();
});
load();
setInterval(load, 15000);
