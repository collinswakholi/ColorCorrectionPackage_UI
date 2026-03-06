import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import ColorCorrectionUI from './ColorCorrectionUI.jsx'
import { ToastProvider } from './components/Toast'

createRoot(document.getElementById('root')).render(
  <StrictMode>
    <ToastProvider>
      <ColorCorrectionUI />
    </ToastProvider>
  </StrictMode>,
)
