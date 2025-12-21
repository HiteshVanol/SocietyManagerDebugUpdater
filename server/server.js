const express = require('express');
const path = require('path');
const fs = require('fs');
const auth = require('basic-auth');
const mysql = require('mysql2/promise'); // Changed from PlanetScale to MySQL

const app = express();
app.use(express.urlencoded({ extended: true }));
app.use(express.json());
app.use('/files', express.static(path.join(__dirname, 'files')));

// Ensure files directory exists
if (!fs.existsSync(path.join(__dirname, 'files'))) {
    fs.mkdirSync(path.join(__dirname, 'files'));
}

// Railway MySQL Connection Function (with SSL fix)
const getDbConnection = async () => {
  const config = {
    host: process.env.DB_HOST || 'localhost',
    user: process.env.DB_USERNAME || 'root',
    password: process.env.DB_PASSWORD || '',
    database: process.env.DB_NAME || 'society_updater',
    port: process.env.DB_PORT || 3306,
    connectTimeout: 10000,
    // 👇 IMPORTANT: SSL certificate issue fix for Railway
    ssl: {
      rejectUnauthorized: false
    }
  };
  
  try {
    const connection = await mysql.createConnection(config);
    return connection;
  } catch (error) {
    console.error('MySQL Connection Error:', error.message);
    throw error;
  }
};

// Admin auth middleware
const requireAuth = (req, res, next) => {
  const user = auth(req);
  if (!user || user.name !== process.env.ADMIN_USER || user.pass !== process.env.ADMIN_PASS) {
    res.set('WWW-Authenticate', 'Basic realm="Admin Panel"');
    return res.status(401).send('Authentication required.');
  }
  next();
};

// Serve admin panel
app.get('/admin', requireAuth, async (req, res) => {
  try {
    const connection = await getDbConnection();
    const [rows] = await connection.execute(
      "SELECT society_code, society_english_name, union_name FROM society_master ORDER BY union_name, society_english_name"
    );
    await connection.end();
    
    const societies = rows || [];
    let societyOptions = societies.map(s => 
      `<option value="${s.society_code}">${s.union_name} - ${s.society_english_name} (${s.society_code})</option>`
    ).join('');

    res.send(`
    <!DOCTYPE html>
    <html>
    <head><title>Society Updater Admin</title></head>
    <body>
      <h2>Society Manager Debug Updater Admin</h2>
      
      <h3>1. Force Update ALL Clients</h3>
      <form method="POST" action="/admin/force">
        <input type="hidden" name="type" value="global">
        <button type="submit" style="background:#d32f2f;color:white;padding:10px;">FORCE UPDATE EVERYONE</button>
      </form>

      <h3>2. Force Update by UNION</h3>
      <form method="POST" action="/admin/force" style="margin:10px 0;">
        <select name="union" required>
          <option value="">Select Union</option>
          <option value="Surendranagar">Surendranagar</option>
          <option value="Sabar">Sabar</option>
          <option value="Bhavnagar">Bhavnagar</option>
          <option value="Porbandar">Porbandar</option>
        </select>
        <input type="hidden" name="type" value="union">
        <button type="submit">Force Union Update</button>
      </form>

      <h3>3. Force Update by SOCIETY</h3>
      <form method="POST" action="/admin/force">
        <select name="societyCode" required>
          <option value="">Select Society</option>
          ${societyOptions}
        </select>
        <input type="hidden" name="type" value="society">
        <button type="submit">Force Society Update</button>
      </form>

      <h3>Recent Actions</h3>
      <div id="log" style="background:#f5f5f5;padding:10px;max-height:300px;overflow:auto;"></div>
      
      <script>
        async function loadLog() {
          const res = await fetch('/api/log');
          const html = await res.text();
          document.getElementById('log').innerHTML = html;
        }
        loadLog();
        setInterval(loadLog, 10000);
      </script>
    </body>
    </html>
    `);
  } catch (e) {
    console.error(e);
    res.status(500).send('Database error: ' + e.message);
  }
});

// Force update endpoint
app.post('/admin/force', requireAuth, async (req, res) => {
  const { type, union, societyCode } = req.body;
  let msg = '', target = '';

  try {
    if (type === 'global') {
      fs.writeFileSync(path.join(__dirname, 'files', 'global_force.flag'), Date.now().toString());
      msg = 'Global force triggered';
      target = 'ALL';
    }
    else if (type === 'union' && union) {
      fs.writeFileSync(path.join(__dirname, 'files', `union_${union}_force.flag`), Date.now().toString());
      msg = `Union force: ${union}`;
      target = union;
    }
    else if (type === 'society' && societyCode) {
      fs.writeFileSync(path.join(__dirname, 'files', `society_${societyCode}_force.flag`), Date.now().toString());
      msg = `Society force: ${societyCode}`;
      target = societyCode;
    }
    else {
      return res.status(400).send('Invalid parameters');
    }

    // Log to Railway MySQL
    const connection = await getDbConnection();
    await connection.execute(
      "INSERT INTO admin_actions (action_type, target_value, triggered_by) VALUES (?, ?, ?)",
      [type, target, 'admin']
    );
    await connection.end();

    res.send(`${msg}! <a href="/admin">Back to Admin Panel</a>`);
  } catch (e) {
    console.error(e);
    res.status(500).send('Server error: ' + e.message);
  }
});

// API to receive client logs
app.post('/api/log', async (req, res) => {
  try {
    const { clientId, societyCode, societyName, unionName, status, errorMessage, versionFileName } = req.body;
    
    const connection = await getDbConnection();
    await connection.execute(
      `INSERT INTO central_debug_update_history 
      (client_id, society_code, society_english_name, union_name, update_date, update_time, version_file_name, status, error_message)
      VALUES (?, ?, ?, ?, CURDATE(), CURTIME(), ?, ?, ?)`,
      [
        clientId || 'unknown',
        societyCode || null,
        societyName || null,
        unionName || null,
        versionFileName || 'unknown',
        status || 'logged',
        errorMessage || null
      ]
    );
    await connection.end();
    
    res.json({ success: true });
  } catch (e) {
    console.error(e);
    res.status(500).json({ error: 'Log failed: ' + e.message });
  }
});

// API to get recent logs
app.get('/api/log', async (req, res) => {
  try {
    const connection = await getDbConnection();
    const [rows] = await connection.execute(
      "SELECT * FROM admin_actions ORDER BY created_on DESC LIMIT 20"
    );
    await connection.end();
    
    const logs = (rows || []).map(row => 
      `[${new Date(row.created_on).toLocaleString()}] ${row.action_type} → ${row.target_value}`
    ).join('<br>');
    res.send(logs);
  } catch (e) {
    console.error(e);
    res.send('Log loading error: ' + e.message);
  }
});

// Health check
app.get('/health', (req, res) => {
  res.json({ status: 'OK', time: new Date().toISOString() });
});

const PORT = process.env.PORT || 10000;
app.listen(PORT, () => {
  console.log(`Server running on port ${PORT}`);
});
