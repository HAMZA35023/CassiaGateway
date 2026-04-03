import { Component, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { LayoutService } from '../../services/layout.service';

@Component({
  selector: 'app-welcome',
  templateUrl: './welcome.html',
  styleUrls: ['./welcome.css']
})
export class WelcomeComponent implements OnInit {
  constructor(private router: Router, private layoutService: LayoutService) {}

  ngOnInit(): void {
    this.navigate();
  }

  navigate() {
    this.router.navigate([this.layoutService.isMobile ? '/mobile-dashboard' : '/dashboard']);
  }
}
