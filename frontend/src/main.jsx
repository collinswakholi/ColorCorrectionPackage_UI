import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import ColorCorrectionUI from './ColorCorrectionUI.jsx'

createRoot(document.getElementById('root')).render(
  <StrictMode>
    <ColorCorrectionUI />
  </StrictMode>,
)
