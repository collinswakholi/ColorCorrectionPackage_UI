# Build Instructions

## Prerequisites
- Python 3.8+
- Node.js 16+
- npm or yarn

## Windows Build

1. **Install Dependencies**
   ```bash
   # Frontend
   cd frontend
   npm install
   
   # Backend
   cd ../backend
   pip install -r requirements.txt
   pip install pyinstaller
   ```

2. **Build Frontend**
   ```bash
   cd frontend
   npm run build
   ```

3. **Build Backend Executable**
   ```bash
   cd backend
   pyinstaller -y colorcorrector.spec
   ```

4. **Output**
   - Executable: `backend/dist/colorcorrector.exe`
   - Frontend: Already bundled in executable

5. **Run**
   ```bash
   run_app.bat
   ```

## macOS Build

1. **Install Dependencies**
   ```bash
   # Frontend
   cd frontend
   npm install
   
   # Backend
   cd backend
   pip3 install -r requirements.txt
   pip3 install pyinstaller
   ```

2. **Build Frontend**
   ```bash
   cd frontend
   npm run build
   ```

3. **Build Backend Executable**
   ```bash
   cd backend
   pyinstaller -y colorcorrector.spec
   ```

4. **Output**
   - Executable: `backend/dist/colorcorrector` (Unix executable)
   - Frontend: Already bundled in executable

5. **Create Run Script**
   ```bash
   chmod +x run_app.sh
   ./run_app.sh
   ```

## Linux Build

1. **Install Dependencies**
   ```bash
   # Frontend
   cd frontend
   npm install
   
   # Backend
   cd backend
   pip3 install -r requirements.txt
   pip3 install pyinstaller
   ```

2. **Build Frontend**
   ```bash
   cd frontend
   npm run build
   ```

3. **Build Backend Executable**
   ```bash
   cd backend
   pyinstaller -y colorcorrector.spec
   ```

4. **Output**
   - Executable: `backend/dist/colorcorrector` (Unix executable)
   - Frontend: Already bundled in executable

5. **Create Run Script**
   ```bash
   chmod +x run_app.sh
   ./run_app.sh
   ```

## Notes

- The PyInstaller spec file (`colorcorrector.spec`) automatically bundles the frontend build
- GPU support requires CUDA toolkit (optional)
- For distribution, zip the `backend/dist` folder with all its contents
- The executable includes all Python dependencies and the frontend
