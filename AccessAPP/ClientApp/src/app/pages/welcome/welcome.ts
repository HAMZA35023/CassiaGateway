import { Component } from '@angular/core';
import { Router } from '@angular/router';

@Component({
  selector: 'app-welcome',
  templateUrl: './welcome.html',
  styleUrls: ['./welcome.css']
})
export class WelcomeComponent {
  constructor(private router: Router) {}

  async checkGateway() {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 2000); // 2 seconds timeout

    try {
      const response = await fetch('http://192.168.40.1/cassia/login#/status', {
        method: 'GET',
        mode: 'no-cors',
        signal: controller.signal,
      });

      // Even if CORS blocks response, reaching it proves gateway exists
      clearTimeout(timeout);
      this.router.navigate(['/dashboard']); // navigate to main app
    } catch (error) {
      clearTimeout(timeout);
      alert('No gateway connected.');
    }
  }
}
