#!/usr/bin/env node

/**
 * Color Correction Studio - Application Launcher
 * Starts both backend (Flask) and frontend (Vite dev server)
 * Cross-platform launcher for development
 */

const { spawn } = require('child_process');
const path = require('path');
const os = require('os');

const ROOT_DIR = __dirname;
const BACKEND_DIR = path.join(ROOT_DIR, 'backend');
const FRONTEND_DIR = path.join(ROOT_DIR, 'frontend');

let backendProcess = null;
let frontendProcess = null;

const isWindows = os.platform() === 'win32';

// ANSI color codes for pretty output
const colors = {
  reset: '\x1b[0m',
  bright: '\x1b[1m',
  cyan: '\x1b[36m',
  green: '\x1b[32m',
  yellow: '\x1b[33m',
  red: '\x1b[31m',
  blue: '\x1b[34m',
  magenta: '\x1b[35m'
};

function log(message, color = 'reset') {
  console.log(`${colors[color]}${message}${colors.reset}`);
}

function logSection(title) {
  console.log('\n' + '='.repeat(70));
  log(`  ${title}`, 'bright');
  console.log('='.repeat(70));
}

// Cleanup function
function cleanup() {
  log('\n🛑 Shutting down servers...', 'yellow');
  
  const killProcess = (proc, name) => {
    if (!proc) return Promise.resolve();
    
    return new Promise((resolve) => {
      log(`  ├─ Stopping ${name}...`, 'yellow');
      try {
        if (isWindows) {
          // Windows: Kill entire process tree
          const { execSync } = require('child_process');
          try {
            execSync(`taskkill /pid ${proc.pid} /T /F`, { stdio: 'ignore' });
          } catch (e) {
            // Process might already be dead
          }
        } else {
          // Unix: Send SIGTERM
          proc.kill('SIGTERM');
        }
        setTimeout(resolve, 1000);
      } catch (err) {
        console.error(`Error stopping ${name}:`, err.message);
        resolve();
      }
    });
  };
  
  // Kill both processes
  Promise.all([
    killProcess(frontendProcess, 'frontend'),
    killProcess(backendProcess, 'backend')
  ]).then(() => {
    log('  └─ All servers stopped', 'green');
    log('\n✅ Cleanup complete\n', 'green');
    setTimeout(() => process.exit(0), 500);
  });
}

// Register cleanup handlers
process.on('SIGINT', cleanup);
process.on('SIGTERM', cleanup);
process.on('exit', cleanup);

// Start backend server
function startBackend() {
  return new Promise((resolve, reject) => {
    logSection('🚀 Starting Backend Server (Flask)');
    log(`  Directory: ${BACKEND_DIR}`, 'cyan');
    log(`  Command: python server_enhanced.py`, 'cyan');
    
    const pythonCmd = isWindows ? 'python' : 'python3';
    
    backendProcess = spawn(pythonCmd, ['server_enhanced.py'], {
      cwd: BACKEND_DIR,
      stdio: 'inherit',
      shell: true,
      env: { ...process.env, NO_BROWSER: '1' }
    });
    
    backendProcess.on('error', (err) => {
      log(`\n❌ Backend failed to start: ${err.message}`, 'red');
      reject(err);
    });
    
    backendProcess.on('exit', (code, signal) => {
      // Only show warning for actual errors (non-zero codes excluding SIGINT/SIGTERM)
      if (code !== 0 && code !== null) {
        // Code 0 = clean exit, Code 130 (128+2) = SIGINT, null with signal = killed by signal
        if (signal === 'SIGINT' || signal === 'SIGTERM' || code === 130) {
          log(`\n✅ Backend shutdown complete`, 'green');
        } else {
          log(`\n⚠️  Backend exited with code ${code}`, 'yellow');
        }
      } else if (code === 0) {
        log(`\n✅ Backend shutdown complete`, 'green');
      }
    });
    
    // Wait for backend to be fully ready
    setTimeout(() => {
      log('  ⏳ Waiting for backend to be fully ready...', 'yellow');
      
      // Check if backend is responding
      const checkBackend = setInterval(() => {
        const http = require('http');
        http.get('http://127.0.0.1:5000/api/health', (res) => {
          if (res.statusCode === 200) {
            clearInterval(checkBackend);
            log('  ✓ Backend server ready on http://127.0.0.1:5000', 'green');
            resolve();
          }
        }).on('error', () => {
          // Backend not ready yet, keep waiting
        });
      }, 500);
      
      // Timeout after 30 seconds
      setTimeout(() => {
        clearInterval(checkBackend);
        log('  ⚠️  Backend took too long to start, proceeding anyway...', 'yellow');
        resolve();
      }, 30000);
    }, 2000);
  });
}

// Start frontend dev server
function startFrontend() {
  return new Promise((resolve, reject) => {
    logSection('🎨 Starting Frontend Dev Server (Vite)');
    log(`  Directory: ${FRONTEND_DIR}`, 'cyan');
    log(`  Command: npm run dev`, 'cyan');
    
    const npmCmd = isWindows ? 'npm.cmd' : 'npm';
    
    frontendProcess = spawn(npmCmd, ['run', 'dev'], {
      cwd: FRONTEND_DIR,
      stdio: 'inherit',
      shell: true
    });
    
    frontendProcess.on('error', (err) => {
      log(`\n❌ Frontend failed to start: ${err.message}`, 'red');
      reject(err);
    });
    
    frontendProcess.on('exit', (code, signal) => {
      // Only trigger cleanup for unexpected exits
      if (code !== 0 && code !== null && !signal) {
        log(`\n⚠️  Frontend exited unexpectedly with code ${code}`, 'yellow');
        cleanup();
      } else if (code === 0) {
        log(`\n✅ Frontend stopped cleanly`, 'green');
      }
    });
    
    // Wait for Vite to start
    setTimeout(() => {
      log('  ✓ Frontend dev server started on http://localhost:5173', 'green');
      resolve();
    }, 3000);
  });
}

// Main execution
async function main() {
  try {
    console.clear();
    logSection('🎨 Color Correction Studio - Development Launcher');
    log('  Starting both backend and frontend servers...', 'cyan');
    
    // Start backend first
    await startBackend();
    
    // Then start frontend
    await startFrontend();
    
    // Success message
    logSection('✅ Application Ready!');
    log('  📍 Backend API:  http://127.0.0.1:5000', 'green');
    log('  📍 Frontend UI:  http://localhost:5173', 'green');
    log('\n  💡 Press Ctrl+C to stop all servers', 'yellow');
    console.log('='.repeat(70) + '\n');
    
    // Open browser after a short delay
    setTimeout(() => {
      const openCmd = isWindows ? 'start' : (os.platform() === 'darwin' ? 'open' : 'xdg-open');
      const url = 'http://localhost:5173';
      
      log(`🌐 Opening browser at ${url}...`, 'blue');
      spawn(openCmd, [url], { shell: true, detached: true });
    }, 1000);
    
  } catch (error) {
    log(`\n❌ Failed to start application: ${error.message}`, 'red');
    cleanup();
    process.exit(1);
  }
}

// Run
main();
