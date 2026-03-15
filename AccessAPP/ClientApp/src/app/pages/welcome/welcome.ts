import { Component } from '@angular/core';
import { Router } from '@angular/router';

@Component({
  selector: 'app-welcome',
  templateUrl: './welcome.html',
  styleUrls: ['./welcome.css']
})
export class WelcomeComponent {
  constructor(private router: Router) {}

  navigate() {
    this.router.navigate(['/dashboard']);
  }
}
