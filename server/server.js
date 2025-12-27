const express = require('express');
const path = require('path');
const fs = require('fs');
const auth = require('basic-auth');
const mysql = require('mysql2/promise');

const app = express();

/* ---------------- BASIC SETUP ---------------- */
app.use(express.json());
app.use(express.urlencoded({ extended: true }));

/* ---------------- FILE DIRECTORY ---------------- */
const FILE_DIR = path.join(__dirname, 'files');
if (!fs.existsSync(FILE_DIR)) fs.mkdirSync(FILE_DIR);

/* ---------------- TLS SAFE HEADERS ---------------- */
app.use((req, res, next) => {
  res.setHeader('Cache-Control', 'no-store');
  res.setHeader('Pragma', 'no-cache');
  res.setHeader('Expires', '0');
  next();
});

/* ---------------- DATABASE ---------------- */
const getDbConnection = async () => {
  return mysql.createConnection({
    host: process.env.DB_HOST,
    user: process.env.DB_USERNAME,
    password: process.env.DB_PASSWORD,
    database: process.env.DB_NAME,
    port: process.env.DB_PORT || 3306,
    ssl: { rejectUnauthorized: false }
  });
};

/* ---------------- AUTH ---------------- */
const requireAuth = (req, res, next) => {
  const user = auth(req);
  if (!user || user.name !== process.env.ADMIN_USER || user.pass !== process.env.ADMIN_PASS) {
    res.set('WWW-Authenticate', 'Basic realm="Updater Admin"');
    return res.status(401).send('Auth required');
  }
  next();
};

/* ---------------- FILE DOWNLOAD (IMPORTANT) ---------------- */
app.get('/download/:file', (req, res) => {
  const filePath = path.join(FILE_DIR, req.params.file);

  if (!fs.existsSync(filePath)) {
    return res.status(404).send('File not found');
  }

  const stat = fs.statSync(filePath);
  const total = stat.size;
  const range = req.headers.range;

  res.setHeader('Content-Type', 'application/octet-stream');
  res.setHeader('Content-Disposition', `attachment; filename="${req.params.file}"`);
  res.setHeader('Accept-Ranges', 'bytes');

  if (range) {
    const parts = range.replace(/bytes=/, '').split('-');
    const start = parseInt(parts[0], 10);
    const end = parts[1] ? parseInt(parts[1], 10) : total - 1;

    res.writeHead(206, {
      'Content-Range': `bytes ${start}-${end}/${total}`,
      'Content-Length': end - start + 1
    });

    fs.createReadStream(filePath, { start, end }).pipe(res);
  } else {
    res.writeHead(200, { 'Content-Length': total });
    fs.createReadStream(filePath).pipe(res);
  }
});

/* ---------------- ADMIN PANEL ---------------- */
app.get('/admin', requireAuth, async (req, res) => {
  res.send('<h2>Updater Admin Running</h2>');
});

/* ---------------- FORCE FLAGS ---------------- */
app.post('/admin/force', requireAuth, (req, res) => {
  fs.writeFileSync(path.join(FILE_DIR, 'global_force.flag'), Date.now().toString());
  res.send('Force update triggered');
});

/* ---------------- CLIENT LOG API ---------------- */
app.post('/api/log', async (req, res) => {
  try {
    const conn = await getDbConnection();
    await conn.execute(
      `INSERT INTO central_debug_update_history
      (client_id, status, error_message, update_date, update_time)
      VALUES (?, ?, ?, CURDATE(), CURTIME())`,
      [
        req.body.clientId || 'unknown',
        req.body.status || 'ok',
        req.body.errorMessage || null
      ]
    );
    await conn.end();
    res.json({ success: true });
  } catch (e) {
    res.status(500).json({ error: e.message });
  }
});

/* ---------------- HEALTH ---------------- */
app.get('/health', (req, res) => {
  res.json({ status: 'OK' });
});

/* ---------------- START ---------------- */
const PORT = process.env.PORT || 10000;
app.listen(PORT, () => {
  console.log(`Updater Server running on port ${PORT}`);
});
