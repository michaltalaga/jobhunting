import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Route, Routes } from 'react-router';
import { JobsProvider } from './jobs';
import { JobList } from './JobList';
import { JobDetailPage } from './JobDetail';
import './styles.css';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <JobsProvider>
        <Routes>
          <Route path="/" element={<JobList />} />
          <Route path="/jobs/:id" element={<JobDetailPage />} />
        </Routes>
      </JobsProvider>
    </BrowserRouter>
  </StrictMode>,
);
