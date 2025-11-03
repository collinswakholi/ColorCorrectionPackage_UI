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

## Docker image

We provide a Dockerfile at `backend/Dockerfile` that builds the frontend, copies it into the backend, installs Python runtime dependencies and runs the Flask app.

Build and run locally:

```bash
# Build the image (from repo root)
docker build -t collins137/colorcorrector:local -f backend/Dockerfile .

# Run the container (maps host 5000 -> container 5000)
docker run --rm -p 5000:5000 collins137/colorcorrector:local
```

Notes about pushing to Docker Hub
- The supplied GitHub Actions workflow (`.github/workflows/windows-docker.yml`) will build a Docker image and push to Docker Hub when the repository has the following repository secrets configured:
   - `DOCKERHUB_USERNAME` — your Docker Hub username (e.g., `collins137`)
   - `DOCKERHUB_TOKEN` — a Docker Hub access token or password

Once secrets are set, pushing to `main` (or manually triggering the workflow) will build and push the image `collins137/colorcorrector:latest`.

## GitHub Actions CI

We added two CI workflows:

- `.github/workflows/build-artifacts.yml` — builds frontend, bundles it into the backend, runs PyInstaller across a matrix (linux/macos/windows) and uploads build artifacts.
- `.github/workflows/windows-docker.yml` — builds a Windows EXE (PyInstaller) and separately builds/pushes a Docker image. The push step requires `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN` repository secrets.

If you want me to trigger a CI run (push the branch and watch the Actions run), I can push the changes and monitor the run. Note: I cannot push Docker images to your Docker Hub without the repository secrets.
