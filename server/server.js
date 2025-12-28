const express = require('express');
const path = require('path');
const fs = require('fs');
const auth = require('basic-auth');
const mysql = require('mysql2/promise');
const crypto = require('crypto');
app.disable('etag');
app.disable('x-powered-by');


const app = express();

/* ---------------- BASIC SETUP ---------------- */
app.use(express.json());
app.use(express.urlencoded({ extended: true }));

/* ---------------- FILE DIRECTORY ---------------- */
const FILE_DIR = path.join(__dirname, 'files');
if (!fs.existsSync(FILE_DIR)) fs.mkdirSync(FILE_DIR);


/* ---------------- FILE DOWNLOAD (UPDATER SAFE) ---------------- */
app.get('/download/:file', (req, res) => {
  const filePath = path.join(FILE_DIR, req.params.file);

  if (!fs.existsSync(filePath)) {
    return res.status(404).send('File not found');
  }

  res.setHeader('Content-Type', 'application/octet-stream');
  res.setHeader('Content-Disposition', `attachment; filename="${req.params.file}"`);
  res.setHeader('Content-Length', fs.statSync(filePath).size);
  res.setHeader('Accept-Ranges', 'bytes');

  const stream = fs.createReadStream(filePath);
  stream.pipe(res);
});

/* ---------------- TLS SAFE HEADERS ---------------- */
app.use((req, res, next) => {
  res.setHeader('Cache-Control', 'no-store');
  res.setHeader('Pragma', 'no-cache');
  res.setHeader('Expires', '0');
  next();
});

/* ---------------- FILE SHA API ---------------- */
app.get('/api/file-sha/:file', (req, res) => {
  const filePath = path.join(FILE_DIR, req.params.file);
  if (!fs.existsSync(filePath)) {
    return res.status(404).json({ error: 'File not found' });
  }

  const hash = crypto.createHash('sha256');
  fs.createReadStream(filePath)
    .on('data', d => hash.update(d))
    .on('end', () => res.json({ sha256: hash.digest('hex') }));
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
