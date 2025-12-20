# Society Manager Updater - Setup Guide

## 🌐 Part 1: PlanetScale Database Setup

1. Go to [planetscale.com](https://planetscale.com) → Sign up with GitHub
2. Create new database: `society-updater`
3. Go to **Connect** → **Password** → Create password → **SAVE CREDENTIALS**
4. In **Console**, run:

```sql
CREATE TABLE society_master (
    society_code VARCHAR(50) PRIMARY KEY,
    society_english_name VARCHAR(255),
    union_name VARCHAR(100)
);

CREATE TABLE central_debug_update_history (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    client_id VARCHAR(255),
    society_code VARCHAR(50),
    society_english_name VARCHAR(255),
    union_name VARCHAR(100),
    update_date DATE,
    update_time TIME,
    version_file_name VARCHAR(255),
    status VARCHAR(50),
    error_message TEXT,
    created_on DATETIME DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE admin_actions (
    id BIGINT PRIMARY KEY AUTO_INCREMENT,
    action_type VARCHAR(50),
    target_value VARCHAR(100),
    triggered_by VARCHAR(100),
    created_on DATETIME DEFAULT CURRENT_TIMESTAMP
);
